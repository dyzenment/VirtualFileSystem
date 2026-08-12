namespace Dytools.VirtualFileSystem.Tests;

public sealed class ReadWriteTests
{
    // -- Basic write → read ----------------------------------------------------

    [Fact]
    public async Task Write_ThenRead_ReturnsContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/hello.txt", "Hello, VFS!");
        var result = await VfsFactory.ReadTextAsync(vfs, "/a/hello.txt");
        Assert.Equal("Hello, VFS!", result);
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsNull()
    {
        var vfs = VfsFactory.CreateDual();
        var stream = await vfs.OpenReadAsync("/a/missing.txt");
        Assert.Null(stream);
    }

    [Fact]
    public async Task Write_Overwrites_ExistingContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "first");
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "second");
        var result = await VfsFactory.ReadTextAsync(vfs, "/a/file.txt");
        Assert.Equal("second", result);
    }

    [Fact]
    public async Task Write_Append_AppendsToExistingContent()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "Hello");

        // CaptureStream commits on Dispose - read only after the using block closes.
        await using (var stream = await vfs.OpenWriteAsync("/a/file.txt", VfsWriteMode.Append))
        {
            await using var writer = new StreamWriter(stream, leaveOpen: true);
            await writer.WriteAsync(", World");
        }

        var result = await VfsFactory.ReadTextAsync(vfs, "/a/file.txt");
        Assert.Equal("Hello, World", result);
    }

    [Fact]
    public async Task Write_CreateNew_ThrowsIfFileExists()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "data");
        await Assert.ThrowsAsync<IOException>(
            () => vfs.OpenWriteAsync("/a/file.txt", VfsWriteMode.CreateNew));
    }

    [Fact]
    public async Task Write_CreateNew_SucceedsForNewFile()
    {
        var vfs = VfsFactory.CreateDual();
        await using var stream = await vfs.OpenWriteAsync("/a/new.txt", VfsWriteMode.CreateNew);
        Assert.NotNull(stream);
    }

    // -- Typed send / retrieve -------------------------------------------------

    [Fact]
    public async Task SendAsync_RetrieveAsync_RoundTripsRecord()
    {
        var vfs = VfsFactory.CreateDual();
        var payload = new TestRecord("localhost", 8080);
        await vfs.SendAsync("/a/config.json", payload);
        var result = await vfs.RetrieveAsync<TestRecord>("/a/config.json");
        Assert.NotNull(result);
        Assert.Equal("localhost", result.Host);
        Assert.Equal(8080, result.Port);
    }

    [Fact]
    public async Task RetrieveAsync_MissingFile_ReturnsDefault()
    {
        var vfs = VfsFactory.CreateDual();
        var result = await vfs.RetrieveAsync<TestRecord>("/a/missing.json");
        Assert.Null(result);
    }

    // -- Multi-file isolation --------------------------------------------------

    [Fact]
    public async Task TwoFiles_DoNotInterfere()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/a.txt", "aaa");
        await VfsFactory.WriteTextAsync(vfs, "/a/b.txt", "bbb");
        Assert.Equal("aaa", await VfsFactory.ReadTextAsync(vfs, "/a/a.txt"));
        Assert.Equal("bbb", await VfsFactory.ReadTextAsync(vfs, "/a/b.txt"));
    }

    [Fact]
    public async Task TwoMounts_StoreSeparately()
    {
        var vfs = VfsFactory.CreateDual();
        await VfsFactory.WriteTextAsync(vfs, "/a/file.txt", "from-a");
        await VfsFactory.WriteTextAsync(vfs, "/b/file.txt", "from-b");
        Assert.Equal("from-a", await VfsFactory.ReadTextAsync(vfs, "/a/file.txt"));
        Assert.Equal("from-b", await VfsFactory.ReadTextAsync(vfs, "/b/file.txt"));
    }

    private sealed record TestRecord(string Host, int Port);
}
