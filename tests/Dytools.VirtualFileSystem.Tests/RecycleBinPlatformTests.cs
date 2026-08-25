using System.Globalization;
using Dytools.VirtualFileSystem.Nodes.LocalFs.Trash;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// The real OS providers. Each test that actually sends something to a bin cleans up after itself
/// where the platform tells us where the entry landed.
/// </summary>
/// <remarks>
/// Tests gated on a platform return early elsewhere rather than failing - the interop they cover
/// cannot be exercised off it, and a red suite on the other two OSes would say nothing true.
/// </remarks>
[Collection(TrashEnvironmentCollection.Name)]
public sealed class RecycleBinPlatformTests
{
    [Fact]
    public void CanRecycle_IsCheapAndNeverThrows()
    {
        // Every provider, including the fallback, must answer rather than blow up - the capability
        // query and RecycleIfAvailable both depend on it.
        var probe = Path.Combine(Path.GetTempPath(), "vfs-probe-that-does-not-exist.txt");
        _ = TrashProviders.Current.CanRecycle(probe);
    }

    [Fact]
    public void Provider_MatchesTheRunningPlatform()
    {
        var provider = TrashProviders.Current;

        if (OperatingSystem.IsWindows())    Assert.IsType<WindowsTrashProvider>(provider);
        else if (OperatingSystem.IsMacOS()) Assert.IsType<MacTrashProvider>(provider);
        else if (OperatingSystem.IsLinux()) Assert.IsType<FreeDesktopTrashProvider>(provider);
        else                                Assert.False(provider.IsSupported);
    }

    // -- macOS: the Objective-C runtime interop --------------------------------

    [Fact]
    public void MacOS_TrashItem_MovesTheFileAndReportsWhereItWent()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var file = Path.Combine(Path.GetTempPath(), $"vfs-trash-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "recycle me");

        var landed = TrashProviders.Current.Trash(file);
        try
        {
            Assert.False(File.Exists(file));

            // trashItemAtURL: hands back the resulting NSURL. Getting a readable path out of it is
            // also the proof that the BOOL return and the NSError out-param were marshalled right.
            Assert.NotNull(landed);
            Assert.True(File.Exists(landed));
            Assert.Equal("recycle me", File.ReadAllText(landed!));
        }
        finally
        {
            // Do not leave test litter in the developer's Trash.
            if (landed is not null && File.Exists(landed)) File.Delete(landed);
        }
    }

    [Fact]
    public void MacOS_TrashMissingItem_ThrowsWithTheSystemsOwnMessage()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var missing = Path.Combine(Path.GetTempPath(), $"vfs-absent-{Guid.NewGuid():N}.txt");

        var ex = Assert.Throws<IOException>(() => TrashProviders.Current.Trash(missing));

        // The NSError was read back rather than swallowed - an empty message would mean the
        // out-parameter slot never came through.
        Assert.True(ex.Message.Length > missing.Length + 30, ex.Message);
    }

    // -- Windows: the shell operation ------------------------------------------

    [Fact]
    public void Windows_TrashItem_RemovesItFromDisk()
    {
        if (!OperatingSystem.IsWindows()) return;

        var file = Path.Combine(Path.GetTempPath(), $"vfs-trash-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "recycle me");

        // SHFileOperationW does not report the $R name it chose, so "gone from disk" is as far as
        // this can assert without walking the bin through COM.
        Assert.Null(TrashProviders.Current.Trash(file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Windows_UncPaths_ReportNoBin()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The single most important false answer: the shell would permanently destroy this.
        Assert.False(TrashProviders.Current.CanRecycle(@"\\server\share\file.txt"));
    }
}
