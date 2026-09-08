using Dytools.VirtualFileSystem.Nodes.LocalFs;

namespace Dytools.VirtualFileSystem.Tests;

/// <summary>
/// The Windows volume tier. LocalFsVolumes is pure string handling with no OS calls precisely so
/// these run everywhere - the conventions are what matter, and they cannot be exercised on a
/// machine that has no drive letters any other way.
/// </summary>
public sealed class LocalFsVolumeTests
{
    [Theory]
    [InlineData("c/Users/mike/x.txt",       @"C:\Users\mike\x.txt")]
    [InlineData("C/Users/mike/x.txt",       @"C:\Users\mike\x.txt")]
    [InlineData("d/data",                   @"D:\data")]
    [InlineData("c",                        @"C:\")]
    [InlineData("unc/server/share/f.txt",   @"\\server\share\f.txt")]
    [InlineData("unc/server/share",         @"\\server\share")]
    public void ToHostPath_MapsTheVolumeSegment(string relative, string expected)
        => Assert.Equal(expected, LocalFsVolumes.ToHostPath(relative));

    [Theory]
    [InlineData("")]                    // the tier root is not a place
    [InlineData("Users/mike/x.txt")]    // no volume segment - nowhere to be on Windows
    [InlineData("data/x.txt")]
    [InlineData("unc")]                 // a UNC needs at least a server
    public void ToHostPath_RejectsWhatDoesNotNameAVolume(string relative)
        => Assert.Null(LocalFsVolumes.ToHostPath(relative));

    [Theory]
    [InlineData(@"C:\Users\mike\x.txt",         "c/Users/mike/x.txt")]
    [InlineData(@"c:\Users\mike\x.txt",         "c/Users/mike/x.txt")]
    [InlineData(@"C:\",                         "c")]
    [InlineData("C:",                           "c")]
    [InlineData(@"\\server\share\f.txt",        "unc/server/share/f.txt")]
    [InlineData(@"\\?\C:\long\path.txt",        "c/long/path.txt")]
    [InlineData(@"\\?\UNC\server\share\f.txt",  "unc/server/share/f.txt")]
    public void TryToRelative_MapsAHostPathBack(string host, string expected)
    {
        Assert.True(LocalFsVolumes.TryToRelative(host, out var relative));
        Assert.Equal(expected, relative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path.txt")]
    [InlineData(@"\\")]
    public void TryToRelative_RejectsWhatIsNotAHostPath(string host)
        => Assert.False(LocalFsVolumes.TryToRelative(host, out _));

    [Theory]
    [InlineData(@"C:\Users\mike\x.txt")]
    [InlineData(@"\\server\share\deep\f.txt")]
    [InlineData(@"D:\")]
    public void RoundTrips(string host)
    {
        Assert.True(LocalFsVolumes.TryToRelative(host, out var relative));
        Assert.Equal(host.TrimEnd('\\') is var t && t.EndsWith(':') ? t + @"\" : t,
                     LocalFsVolumes.ToHostPath(relative));
    }

    [Fact]
    public void UncServerNamedLikeADrive_DoesNotCollideWithThatDrive()
    {
        // The reason UNC gets its own tier segment: a server called "c" would otherwise be drive C:.
        Assert.True(LocalFsVolumes.TryToRelative(@"\\c\share\f.txt", out var unc));
        Assert.True(LocalFsVolumes.TryToRelative(@"C:\share\f.txt",  out var drive));
        Assert.NotEqual(unc, drive);
        Assert.Equal("unc/c/share/f.txt", unc);
        Assert.Equal("c/share/f.txt", drive);
    }

    [Theory]
    [InlineData(@"\\?\C:\x",            @"C:\x")]
    [InlineData(@"\\?\UNC\srv\s\f",     @"\\srv\s\f")]
    [InlineData(@"\\.\C:\x",            @"C:\x")]
    [InlineData(@"C:\x",                @"C:\x")]      // untouched when there is no prefix
    [InlineData(@"\\srv\s\f",           @"\\srv\s\f")]
    public void StripExtendedPrefix(string input, string expected)
        => Assert.Equal(expected, LocalFsVolumes.StripExtendedPrefix(input));
}
