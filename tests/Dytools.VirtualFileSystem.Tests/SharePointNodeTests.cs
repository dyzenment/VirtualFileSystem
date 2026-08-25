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
              {"id":"01NEW","name":"new.txt","size":5,"file":{"mimeType":"text/plain"},"parentReference":{"path":"/drives/drive1/root:/docs"}},
              {"id":"01GONE","name":"gone.txt","deleted":{"state":"deleted"},"parentReference":{"path":"/drives/drive1/root:/docs"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=NEXT"}
            """));

        var batch = await Node(handler).GetChangesAsync(null);

        Assert.EndsWith("token=NEXT", batch.Cursor);
        Assert.Equal(2, batch.Changes.Count);   // root skipped

        var created = batch.Changes.Single(c => c.Path == "docs/new.txt");
        Assert.Equal(SharePointChangeType.Updated, created.Type);
        Assert.NotNull(created.Info);
        Assert.Equal("01NEW", created.Id);

        var deleted = batch.Changes.Single(c => c.Id == "01GONE");
        Assert.Equal(SharePointChangeType.Deleted, deleted.Type);
        Assert.Null(deleted.Info);
        Assert.Equal("docs/gone.txt", deleted.Path);   // resolved here because this tombstone carried one

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
        var mirror  = new NodeCatalog(new JsonFileVfsCatalog(new InMemoryKvNode()));
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

    [Fact]
    public async Task Catalog_MultiPageDelta_SeedsEveryPage_AndCheckpointsCursor()
    {
        const string page1 = """
            {"value":[{"name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}],
            "@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=P2"}
            """;
        const string page2 = """
            {"value":[{"name":"b.txt","size":2,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=DONE"}
            """;
        var handler = new StubHandler(req =>
            (HttpStatusCode.OK, req.RequestUri!.ToString().Contains("token=P2") ? page2 : page1));
        var mirror  = new NodeCatalog(new JsonFileVfsCatalog(new InMemoryKvNode()));
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) }, "drive1", null, mirror);

        var root = new List<string>();
        await foreach (var i in node.ListAsync(Req(""), VfsListOptions.Default))
            root.Add(i.RelativePath.ToString());

        Assert.Contains("a.txt", root);            // seeded from page 1
        Assert.Contains("b.txt", root);            // seeded from page 2
        Assert.Equal(2, handler.Requests.Count);   // both delta pages fetched

        // The terminal deltaLink was checkpointed, so a later sync resumes from it (not a fresh delta).
        var before = handler.Requests.Count;
        await node.RefreshAsync();
        Assert.Contains("token=DONE", handler.Requests[before]);
    }

    [Fact]
    public async Task ForSite_ResolvesDriveId_FromSiteAndLibrary_ThenUsesIt()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/drives"))              // list libraries → resolve the drive id
                return (HttpStatusCode.OK, """{"value":[{"id":"b!RESOLVED","name":"Documents"}]}""");
            if (url.Contains("?$select=id"))          // resolve the site id
                return (HttpStatusCode.OK, """{"id":"site-123"}""");
            return (HttpStatusCode.OK, """{"name":"report.pdf","size":1,"file":{}}""");   // the item op
        });

        var node = SharePointNode.ForSite(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "contoso.sharepoint.com:/sites/Marketing", "Documents");

        var info = await node.GetInfoAsync(Req("docs/report.pdf"));

        Assert.NotNull(info);
        Assert.Contains(handler.Requests, r => r.Contains("contoso.sharepoint.com") && r.Contains("Marketing"));
        Assert.Contains(handler.Requests, r => r.Contains("site-123/drives"));
        Assert.Contains(handler.Requests, r => r.Contains("drives/b!RESOLVED/root"));   // used the resolved id
    }

    [Fact]
    public async Task ForSite_ConvertsFullUrl_ToGraphSiteAddress()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/drive?"))      return (HttpStatusCode.OK, """{"id":"b!RESOLVED"}""");
            if (url.Contains("?$select=id"))  return (HttpStatusCode.OK, """{"id":"site-123"}""");
            return (HttpStatusCode.OK, """{"name":"report.pdf","size":1,"file":{}}""");
        });

        // A full browser URL is normalized to Graph's "{host}:/{path}" site address.
        var node = SharePointNode.ForSite(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "https://fslgroup2.sharepoint.com/sites/FSLSW");

        await node.GetInfoAsync(Req("docs/report.pdf"));

        Assert.Contains(handler.Requests,
            r => r.Contains("sites/fslgroup2.sharepoint.com:/sites/FSLSW?$select=id"));
        Assert.DoesNotContain(handler.Requests, r => r.Contains("https://fslgroup2"));   // no raw URL leaked
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/Marketing", "contoso.sharepoint.com:/sites/Marketing")]
    [InlineData("https://contoso.sharepoint.com/sites/Marketing/", "contoso.sharepoint.com:/sites/Marketing")]
    [InlineData("https://contoso.sharepoint.com", "contoso.sharepoint.com")]
    [InlineData("contoso.sharepoint.com:/sites/Marketing", "contoso.sharepoint.com:/sites/Marketing")]
    [InlineData("contoso.sharepoint.com", "contoso.sharepoint.com")]
    public void NormalizeSiteAddress_ConvertsUrls_AndLeavesGraphFormsAlone(string input, string expected)
        => Assert.Equal(expected, SharePointNode.NormalizeSiteAddress(input));

    // -- Item id -> CatalogEntry.ContentId -------------------------------------
    //
    // The mirror keys entries on Graph's driveItem id rather than on their path, because that is the
    // only field a delta tombstone carries. These pin the plumbing that gets the id there; acting on
    // it during a delete is the next step.

    [Fact]
    public async Task GetInfo_CarriesItemIdAsContentId()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK,
            """{"id":"01ITEMID","name":"report.pdf","size":1234,"file":{"mimeType":"application/pdf"}}"""));

        var info = await Node(handler).GetInfoAsync(Req("docs/report.pdf"));

        Assert.Equal("01ITEMID", info!.Properties.GetString(VfsPropertyKeys.ContentId));
    }

    [Fact]
    public async Task Delta_SeedsMirrorWithItemIdInTheIndexedColumn()
    {
        const string delta = """
            {"value":[
              {"id":"01AAA","name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}},
              {"id":"01DOCS","name":"docs","folder":{"childCount":1},"parentReference":{"path":"/drives/drive1/root:"}},
              {"id":"01BBB","name":"b.txt","size":2,"file":{},"parentReference":{"path":"/drives/drive1/root:/docs"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=C1"}
            """;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, delta));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }

        // The id landed in ContentId, so it is reachable by the indexed lookup a tombstone will use.
        var byId = new List<CatalogEntry>();
        await foreach (var e in catalog.ListByContentIdAsync("01BBB")) byId.Add(e);
        Assert.Equal("docs/b.txt", Assert.Single(byId).Path.ToString());

        Assert.Equal("01AAA", (await catalog.GetAsync(VfsPath.From("a.txt")))!.ContentId);
    }

    [Fact]
    public async Task Delta_MirroredListing_StillReportsTheItemId()
    {
        // Round-trip: the id goes into the ContentId column on the way in and has to come back out
        // under the same property key, or a listing served from the mirror loses it.
        const string delta = """
            {"value":[
              {"id":"01AAA","name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, delta));
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(new JsonFileVfsCatalog(new InMemoryKvNode())));

        var listed = new List<VfsNodeInfo>();
        await foreach (var i in node.ListAsync(Req(""), VfsListOptions.Default)) listed.Add(i);

        var a = listed.Single(i => i.RelativePath.ToString() == "a.txt");
        Assert.Equal("01AAA", a.Properties.GetString(VfsPropertyKeys.ContentId));
    }

    [Fact]
    public async Task Delta_ItemWithoutAnId_StillMirrors_WithNoContentId()
    {
        // Defensive: every real driveItem carries an id, but a row missing one must not be dropped.
        const string delta = """
            {"value":[
              {"name":"legacy.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, delta));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }

        var e = await catalog.GetAsync(VfsPath.From("legacy.txt"));
        Assert.NotNull(e);
        Assert.Null(e!.ContentId);
    }


    // -- Deletions (delta tombstones) ------------------------------------------
    //
    // The shape that matters: a real tombstone is an id plus the deleted facet, with no name and a
    // parentReference carrying no path. Requiring either before looking at the facet is what silently
    // dropped every deletion, so these fixtures deliberately carry nothing else.

    [Fact]
    public async Task Delta_BareTombstone_IsReportedAsADeletion()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK,
            """
            {"value":[
              {"id":"01GONE","deleted":{"state":"deleted"},"parentReference":{"driveId":"drive1"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """));

        var change = Assert.Single(await Node(handler).GetChangesAsync(null) is var b ? b.Changes : []);

        Assert.Equal(SharePointChangeType.Deleted, change.Type);
        Assert.Equal("01GONE", change.Id);
        Assert.Null(change.Path);    // nothing to resolve - the id is the only handle
        Assert.Null(change.Info);
    }

    [Fact]
    public async Task Delta_BareTombstone_RemovesTheMirroredRow()
    {
        const string seed = """
            {"value":[
              {"id":"01AAA","name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}},
              {"id":"01BBB","name":"b.txt","size":2,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=C1"}
            """;
        const string tombstone = """
            {"value":[
              {"id":"01BBB","deleted":{"state":"deleted"},"parentReference":{"driveId":"drive1"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/drives/drive1/root/delta?token=C2"}
            """;

        var call    = 0;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, call++ == 0 ? seed : tombstone));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }   // seed
        var after = new List<string>();
        await foreach (var i in node.ListAsync(Req(""), VfsListOptions.Default))       // apply the tombstone
            after.Add(i.RelativePath.ToString());

        Assert.Contains("a.txt", after);
        Assert.DoesNotContain("b.txt", after);
        Assert.Null(await catalog.GetAsync(VfsPath.From("b.txt")));
    }

    [Fact]
    public async Task Delta_TombstoneForSomethingWeNeverMirrored_IsANoOp()
    {
        // The delta feed is drive-wide, so a rooted mount sees deletions from outside its root. With
        // no path on the tombstone there is nothing to filter on up front - it just resolves to nothing.
        const string seed = """
            {"value":[
              {"id":"01AAA","name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """;
        const string tombstone = """
            {"value":[
              {"id":"01ELSEWHERE","deleted":{"state":"deleted"},"parentReference":{"driveId":"drive1"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C2"}
            """;

        var call    = 0;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, call++ == 0 ? seed : tombstone));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }
        var after = new List<string>();
        await foreach (var i in node.ListAsync(Req(""), VfsListOptions.Default)) after.Add(i.RelativePath.ToString());

        Assert.Contains("a.txt", after);   // the unrelated tombstone removed nothing
    }

    [Fact]
    public async Task Delta_RenamedItem_IsRemovedByIdAfterwards()
    {
        // Graph reports a rename as an upsert at the NEW path; the id is unchanged. A later deletion
        // has to find the row wherever it now lives, which is exactly what id-matching buys.
        const string seed = """
            {"value":[
              {"id":"01AAA","name":"before.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """;
        const string renamed = """
            {"value":[
              {"id":"01AAA","name":"after.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C2"}
            """;
        const string tombstone = """
            {"value":[
              {"id":"01AAA","deleted":{"state":"deleted"},"parentReference":{"driveId":"drive1"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C3"}
            """;

        var call    = 0;
        var pages   = new[] { seed, renamed, tombstone };
        var handler = new StubHandler(_ => (HttpStatusCode.OK, pages[Math.Min(call++, pages.Length - 1)]));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }   // seed
        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }   // rename
        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }   // delete

        Assert.Null(await catalog.GetAsync(VfsPath.From("after.txt")));
    }

    [Fact]
    public async Task CachingMount_RequiresAContentAddressedCatalog()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new SharePointNode(
            new HttpClient(new StubHandler(_ => (HttpStatusCode.OK, "{}"))) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(new NamespaceOnlyCatalog())));

        Assert.Contains(nameof(IContentAddressedCatalog), ex.Message);
    }

    // -- Folders and stale aliases ---------------------------------------------

    [Fact]
    public async Task Delta_FolderTombstone_RemovesTheFolderAndItsSubtree()
    {
        // Folder rows carry the driveItem id too, so a folder tombstone - just as bare as a file's -
        // resolves to the folder, and removing it takes the subtree with it.
        const string seed = """
            {"value":[
              {"id":"01DOCS","name":"docs","folder":{"childCount":1},"parentReference":{"path":"/drives/drive1/root:"}},
              {"id":"01BBB","name":"b.txt","size":2,"file":{},"parentReference":{"path":"/drives/drive1/root:/docs"}},
              {"id":"01KEEP","name":"keep.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """;
        const string tombstone = """
            {"value":[
              {"id":"01DOCS","deleted":{"state":"deleted"},"parentReference":{"driveId":"drive1"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C2"}
            """;

        var call    = 0;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, call++ == 0 ? seed : tombstone));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }
        Assert.Equal("01DOCS", (await catalog.GetAsync(VfsPath.From("docs")))!.ContentId);   // folder row keyed by id

        var after = new List<string>();
        await foreach (var i in node.ListAsync(Req(""), new VfsListOptions { Recurse = true }))
            after.Add(i.RelativePath.ToString());

        Assert.DoesNotContain("docs", after);
        Assert.DoesNotContain("docs/b.txt", after);   // the subtree went with it
        Assert.Contains("keep.txt", after);
    }

    [Fact]
    public async Task Delta_ExternalRename_LeavesNoRowAtTheOldPath()
    {
        // Graph reports a rename as an upsert at the NEW path and never mentions the old one, so
        // without id-matching the old row would sit there as a phantom until something tripped on it.
        const string seed = """
            {"value":[
              {"id":"01AAA","name":"before.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """;
        const string renamed = """
            {"value":[
              {"id":"01AAA","name":"after.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C2"}
            """;

        var call    = 0;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, call++ == 0 ? seed : renamed));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }
        var after = new List<string>();
        await foreach (var i in node.ListAsync(Req(""), VfsListOptions.Default)) after.Add(i.RelativePath.ToString());

        Assert.Contains("after.txt", after);
        Assert.DoesNotContain("before.txt", after);
        Assert.Null(await catalog.GetAsync(VfsPath.From("before.txt")));

        var rows = new List<CatalogEntry>();
        await foreach (var e in catalog.ListByContentIdAsync("01AAA")) rows.Add(e);
        Assert.Equal("after.txt", Assert.Single(rows).Path.ToString());   // one item, one row
    }

    [Fact]
    public async Task Delta_MoveIntoAnotherFolder_LeavesNoRowAtTheOldPath()
    {
        const string seed = """
            {"value":[
              {"id":"01DOCS","name":"docs","folder":{},"parentReference":{"path":"/drives/drive1/root:"}},
              {"id":"01AAA","name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """;
        const string moved = """
            {"value":[
              {"id":"01AAA","name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:/docs"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C2"}
            """;

        var call    = 0;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, call++ == 0 ? seed : moved));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }
        var after = new List<string>();
        await foreach (var i in node.ListAsync(Req(""), new VfsListOptions { Recurse = true }))
            after.Add(i.RelativePath.ToString());

        Assert.Contains("docs/a.txt", after);
        Assert.DoesNotContain("a.txt", after);
    }

    [Fact]
    public async Task Delta_UnchangedItem_KeepsItsSingleRow()
    {
        // The alias sweep must not mistake an item re-reported at the SAME path for a move.
        const string page = """
            {"value":[
              {"id":"01AAA","name":"a.txt","size":1,"file":{},"parentReference":{"path":"/drives/drive1/root:"}}
            ],
            "@odata.deltaLink":"https://x/delta?token=C1"}
            """;
        var handler = new StubHandler(_ => (HttpStatusCode.OK, page));
        var catalog = new JsonFileVfsCatalog(new InMemoryKvNode());
        var node    = new SharePointNode(
            new HttpClient(handler) { BaseAddress = new Uri(GraphHttp_BaseAddress) },
            "drive1", null, new NodeCatalog(catalog));

        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }
        await foreach (var _ in node.ListAsync(Req(""), VfsListOptions.Default)) { }

        Assert.NotNull(await catalog.GetAsync(VfsPath.From("a.txt")));
        var rows = new List<CatalogEntry>();
        await foreach (var e in catalog.ListByContentIdAsync("01AAA")) rows.Add(e);
        Assert.Single(rows);
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

// A catalog with the namespace operations and no content index - what the caching mount rejects.
file sealed class NamespaceOnlyCatalog : IVfsCatalog
{
    private readonly JsonFileVfsCatalog _inner = new(new InMemoryKvNode());

    public ValueTask<CatalogEntry?> GetAsync(VfsPath p, CancellationToken ct = default) => _inner.GetAsync(p, ct);
    public IAsyncEnumerable<CatalogEntry> ListChildrenAsync(VfsPath p, CancellationToken ct = default) => _inner.ListChildrenAsync(p, ct);
    public ValueTask<CatalogEntry?> PutEntryAsync(CatalogEntry e, CancellationToken ct = default) => _inner.PutEntryAsync(e, ct);
    public ValueTask EnsureDirectoryAsync(VfsPath p, DateTimeOffset ts, CancellationToken ct = default) => _inner.EnsureDirectoryAsync(p, ts, ct);
    public IAsyncEnumerable<CatalogEntry> RemoveAsync(VfsPath p, CancellationToken ct = default) => _inner.RemoveAsync(p, ct);
    public ValueTask MoveAsync(VfsPath from, VfsPath to, CancellationToken ct = default) => _inner.MoveAsync(from, to, ct);
}
