using System.Net;
using System.Text;
using Dytools.VirtualFileSystem.Catalog;
using Dytools.VirtualFileSystem.Nodes.InMemory;
using Dytools.VirtualFileSystem.Nodes.SharePoint;

namespace Dytools.VirtualFileSystem.Tests;

// Offline tests for SharePointNode: a stub HttpMessageHandler returns canned Graph JSON, so we
// verify URL construction, driveItem parsing, and delta path resolution without a live tenant.
public sealed class SharePointNodeTests
{
    private static SharePointNode Node(StubHandler handler, string? rootPath = null)
        => new(new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) }, "drive1", rootPath);

    private const string GraphHttp_BaseAddress = "https://graph.microsoft.com/v1.0/";

    private static VfsNodeRequest Req(string rel) => new(VfsPath.From(rel));

    [Fact]
    public async Task GetInfo_ParsesDriveItem_AndBuildsPathUrl()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK,
            """{"name":"report.pdf","size":1234,"eTag":"etag1","file":{"mimeType":"application/pdf"},"lastModifiedDateTime":"2026-01-01T00:00:00Z"}"""));

        var info = await Node(handler).GetInfoAsync(Req("docs/report.pdf"));

        Assert.NotNull(info);
        Assert.True(info!.IsFile);
        Assert.Equal(1234, info.SizeBytes);
        Assert.Equal("etag1", info.Properties.GetString("ETag"));
        Assert.Equal("application/pdf", info.Properties.GetString("ContentType"));
        Assert.Contains("drives/drive1/root:/docs/report.pdf:", handler.Requests[0]);
    }

    [Fact]
    public async Task GetInfo_404_ReturnsNull()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.NotFound, null));
        Assert.Null(await Node(handler).GetInfoAsync(Req("missing.txt")));
    }

    [Fact]
    public async Task List_ReturnsFilesAndFolders()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK,
            """{"value":[{"name":"a.pdf","size":10,"file":{"mimeType":"application/pdf"}},{"name":"sub","folder":{"childCount":2}}]}"""));

        var infos = new List<VfsNodeInfo>();
        await foreach (var i in Node(handler).ListAsync(Req("docs"), VfsListOptions.Default))
            infos.Add(i);

        Assert.Contains(infos, i => i.RelativePath.ToString() == "docs/a.pdf" && i.IsFile);
        Assert.Contains(infos, i => i.RelativePath.ToString() == "docs/sub"   && i.IsDirectory);
        Assert.Contains("drives/drive1/root:/docs:/children", handler.Requests[0]);
    }

    [Fact]
    public async Task Delta_MapsChanges_SkipsRoot_AndReturnsCursor()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK,
            """
            {"value":[
              {"root":{},"name":"root"},
              {"name":"new.txt","size":5,"file":{"mimeType":"text/plain"},"parentReference":{"path":"/drives/drive1/root:/docs"}},
              {"name":"gone.txt","deleted":{"state":"deleted"},"parentReference":{"path":"/drives/drive1/root:/docs"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=NEXT"}
            """));

        var batch = await Node(handler).GetChangesAsync(null);

        Assert.EndsWith("token=NEXT", batch.Cursor);
        Assert.Equal(2, batch.Changes.Count);   // root skipped

        var created = batch.Changes.Single(c => c.Path == "docs/new.txt");
        Assert.Equal(SharePointChangeType.Updated, created.Type);
        Assert.NotNull(created.Info);

        var deleted = batch.Changes.Single(c => c.Path == "docs/gone.txt");
        Assert.Equal(SharePointChangeType.Deleted, deleted.Type);
        Assert.Null(deleted.Info);

        Assert.Contains("drives/drive1/root/delta", handler.Requests[0]);
    }

    [Fact]
    public async Task Delta_RespectsRootPrefix()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK,
            """
            {"value":[
              {"name":"in.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:/Shared/Reports"}},
              {"name":"out.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:/Other"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C"}
            """));

        // Mount rooted at "Shared/Reports": only changes under it, re-based to mount-relative.
        var batch = await Node(handler, rootPath: "Shared/Reports").GetChangesAsync(null);

        Assert.Single(batch.Changes);
        Assert.Equal("in.txt", batch.Changes[0].Path);
    }

    [Fact]
    public async Task Catalog_ListServesFromMirror_AfterDeltaSync()
    {
        const string delta = """
            {"value":[
              {"name":"a.txt","size":1,"file":{"mimeType":"text/plain"},"parentReference":{"path":"/drives/drive1/root:"}},
              {"name":"docs","folder":{"childCount":1},"parentReference":{"path":"/drives/drive1/root:"}},
              {"name":"b.txt","size":2,"file":{"mimeType":"text/plain"},"parentReference":{"path":"/drives/drive1/root:/docs"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=C1"}
            """;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, delta));
        var mirror  = new CatalogMirror(new JsonFileVfsCatalog(new InMemoryKvNode()));
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) }, "drive1", null, mirror);

        var root = new List<string>();
        await foreach (var i in node.ListAsync(Req(""), VfsListOptions.Default))
            root.Add(i.RelativePath.ToString());

        Assert.Contains("a.txt", root);
        Assert.Contains("docs",  root);
        Assert.DoesNotContain("b.txt", root);                       // under docs, not a root child
        Assert.DoesNotContain(".vfs-mirror-state", root);           // reserved state entry hidden
        Assert.Single(handler.Requests);                           // only the delta call; listing came from the mirror
        Assert.Contains("root/delta", handler.Requests[0]);

        // Recursive listing is served entirely from the mirror - the only network call is the sync delta.
        var all = new List<string>();
        await foreach (var i in node.ListAsync(Req(""), new VfsListOptions { Recurse = true }))
            all.Add(i.RelativePath.ToString());
        Assert.Contains("docs/b.txt", all);
        Assert.Equal(2, handler.Requests.Count);
    }

    // -- Stub transport --------------------------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Code, string? Body)> responder)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            var (code, body) = responder(request);
            var resp = new HttpResponseMessage(code);
            if (body is not null) resp.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(resp);
        }
    }
}
