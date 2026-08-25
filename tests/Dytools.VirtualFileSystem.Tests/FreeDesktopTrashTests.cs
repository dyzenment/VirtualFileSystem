using System.Globalization;
using Dytools.VirtualFileSystem.Nodes.LocalFs.Trash;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>Serialises the tests that mutate XDG_DATA_HOME / HOME for the current process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TrashEnvironmentCollection
{
    public const string Name = "trash-environment";
}

/// <summary>
/// The freedesktop.org trash layout: where the payload goes, and what the <c>.trashinfo</c> record
/// beside it says.
/// </summary>
/// <remarks>
/// These run on every OS, not just Linux. The parts under test - the <c>files/</c> + <c>info/</c>
/// layout, the <c>Path=</c> encoding, the <c>DeletionDate</c> format, and exclusive name reservation -
/// are plain filesystem work with no Linux-specific calls in them, and pointing
/// <c>XDG_DATA_HOME</c> at a temp directory exercises all of it. What genuinely is Linux-only is
/// volume selection: <c>/proc/self/mounts</c> and <c>/proc/self/status</c> are simply absent
/// elsewhere, the provider falls back to the home trash, and that path is covered on Linux CI.
/// </remarks>
[Collection(TrashEnvironmentCollection.Name)]
// CA1416: the provider is attributed [SupportedOSPlatform("linux")] because that is the only OS it is
// ever *selected* on. Running it elsewhere is exactly what these tests are for - see the remarks
// above - and the Linux-only calls inside it are all already guarded.
#pragma warning disable CA1416
public sealed class FreeDesktopTrashTests : IDisposable
{
    private readonly string  _sandbox = Directory.CreateTempSubdirectory("vfs-fdo-").FullName;
    private readonly string? _previousDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    private readonly string _dataHome;
    private readonly string _workDir;

    private readonly FreeDesktopTrashProvider _provider = new();

    public FreeDesktopTrashTests()
    {
        // Both under one temp root, so the move stays on a single filesystem the way it would on a
        // real desktop - a cross-device home trash is precisely what the spec tells us to avoid.
        _dataHome = Path.Combine(_sandbox, "xdg");
        _workDir  = Path.Combine(_sandbox, "work");
        Directory.CreateDirectory(_dataHome);
        Directory.CreateDirectory(_workDir);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previousDataHome);
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string TrashRoot  => Path.Combine(_dataHome, "Trash");
    private string FilesDir   => Path.Combine(TrashRoot, "files");
    private string InfoDir    => Path.Combine(TrashRoot, "info");

    private string Seed(string name, string content = "bytes")
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // Reads a .trashinfo into its key/value pairs, asserting the section header on the way.
    private Dictionary<string, string> ReadInfo(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(InfoDir, name + ".trashinfo"));
        Assert.Equal("[Trash Info]", lines[0]);

        return lines.Skip(1)
                    .Where(l => l.Length > 0)
                    .Select(l => l.Split('=', 2))
                    .ToDictionary(p => p[0], p => p[1]);
    }

    // -- Layout ----------------------------------------------------------------

    [Fact]
    public void Trash_MovesThePayloadUnderFiles_AndReportsWhere()
    {
        var file = Seed("report.pdf", "pdf bytes");

        var landed = _provider.Trash(file);

        Assert.False(File.Exists(file));
        Assert.Equal(Path.Combine(FilesDir, "report.pdf"), landed);
        Assert.Equal("pdf bytes", File.ReadAllText(landed!));
    }

    [Fact]
    public void Trash_WritesTheOriginalPathAbsolute_ForTheHomeTrash()
    {
        var file = Seed("report.pdf");

        _provider.Trash(file);

        var info = ReadInfo("report.pdf");
        Assert.Equal(file, Uri.UnescapeDataString(info["Path"]));
    }

    [Fact]
    public void Trash_WritesDeletionDateAsLocalTimeWithNoOffset()
    {
        _provider.Trash(Seed("report.pdf"));

        var value = ReadInfo("report.pdf")["DeletionDate"];

        // The spec's format exactly: no 'Z', no offset, no fractional seconds.
        Assert.True(
            DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm:ss",
                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed),
            $"'{value}' is not the spec's DeletionDate format");
        Assert.True(Math.Abs((DateTime.Now - parsed).TotalMinutes) < 5);
    }

    [Fact]
    public void Trash_TakesDirectoriesWhole()
    {
        var dir = Path.Combine(_workDir, "project");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "nested", "leaf.txt"), "leaf");

        _provider.Trash(dir);

        Assert.False(Directory.Exists(dir));
        Assert.Equal("leaf", File.ReadAllText(Path.Combine(FilesDir, "project", "nested", "leaf.txt")));
        Assert.Equal(dir, Uri.UnescapeDataString(ReadInfo("project")["Path"]));
    }

    // -- Encoding --------------------------------------------------------------

    [Fact]
    public void Path_IsPercentEncodedButKeepsSeparators()
    {
        var file = Seed("quarterly report #3.txt");

        _provider.Trash(file);

        var raw = ReadInfo("quarterly report #3.txt")["Path"];

        Assert.DoesNotContain(' ', raw);          // encoded...
        Assert.DoesNotContain('#', raw);
        Assert.Contains('/', raw);                // ...but still reads as a path
        Assert.Equal(file, Uri.UnescapeDataString(raw));
    }

    // -- Name collisions -------------------------------------------------------

    [Fact]
    public void SecondEntryWithTheSameName_GetsADistinctNameAndItsOwnRecord()
    {
        var first = Seed("notes.txt", "first");
        _provider.Trash(first);

        Directory.CreateDirectory(Path.Combine(_workDir, "elsewhere"));
        var second = Path.Combine(_workDir, "elsewhere", "notes.txt");
        File.WriteAllText(second, "second");

        var landed = _provider.Trash(second);

        // The first entry is untouched, and both records survive with their true origins.
        Assert.Equal("first",  File.ReadAllText(Path.Combine(FilesDir, "notes.txt")));
        Assert.Equal("second", File.ReadAllText(landed!));
        Assert.NotEqual(Path.Combine(FilesDir, "notes.txt"), landed);

        Assert.Equal(first,  Uri.UnescapeDataString(ReadInfo("notes.txt")["Path"]));
        Assert.Equal(second, Uri.UnescapeDataString(
            ReadInfo(Path.GetFileName(landed!))["Path"]));
    }

    [Fact]
    public void ExistingPayloadWithNoRecord_IsNotClobbered()
    {
        // Someone else's inconsistent trash: a file under files/ with no info/ entry. Yielding the
        // name is the only safe move - overwriting would destroy data we cannot account for.
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(InfoDir);
        File.WriteAllText(Path.Combine(FilesDir, "notes.txt"), "not ours");

        var landed = _provider.Trash(Seed("notes.txt", "ours"));

        Assert.Equal("not ours", File.ReadAllText(Path.Combine(FilesDir, "notes.txt")));
        Assert.Equal("ours",     File.ReadAllText(landed!));
        Assert.False(File.Exists(Path.Combine(InfoDir, "notes.txt.trashinfo")));
    }

    [Fact]
    public void ManyEntriesWithTheSameName_AllSurvive()
    {
        for (var i = 0; i < 5; i++)
        {
            var dir = Path.Combine(_workDir, $"d{i}");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "same.txt");
            File.WriteAllText(file, $"content-{i}");
            _provider.Trash(file);
        }

        Assert.Equal(5, Directory.GetFiles(FilesDir).Length);
        Assert.Equal(5, Directory.GetFiles(InfoDir, "*.trashinfo").Length);
        Assert.Equal(5, Directory.GetFiles(FilesDir).Select(File.ReadAllText).Distinct().Count());
    }

    // -- Availability ----------------------------------------------------------

    [Fact]
    public void CanRecycle_IsTrueWhenADataHomeIsConfigured()
    {
        Assert.True(_provider.CanRecycle(Seed("probe.txt")));
    }
}
#pragma warning restore CA1416
