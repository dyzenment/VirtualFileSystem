using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// Builds the authed Microsoft Graph HttpClient used by SharePointNode, and the small
// DelegatingHandlers behind it. No SDK: base address + bearer token + light retry, that's all.
internal static class GraphHttp
{
    public const string BaseAddress = "https://graph.microsoft.com/v1.0/";

    public static HttpClient CreateClient(ISharePointTokenProvider tokens)
    {
        var handler = new BearerTokenHandler(tokens)
        {
            InnerHandler = new ThrottleRetryHandler
            {
                InnerHandler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),   // pick up DNS rotation
                },
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri(BaseAddress) };
    }
}

// Attaches the caller's current access token as a bearer on every request. Re-asks the token
// provider per send, so a token refreshed by the caller's system is picked up automatically.
internal sealed class BearerTokenHandler(ISharePointTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}

// Retries 429 / 503 honoring Retry-After, for requests with no body (GET/DELETE) - a body-bearing
// request can't be replayed once its content stream is consumed, so writes surface the error and
// the caller retries the operation.
internal sealed class ThrottleRetryHandler : DelegatingHandler
{
    private const int MaxAttempts = 4;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);

            var throttled = response.StatusCode is HttpStatusCode.TooManyRequests
                                                  or HttpStatusCode.ServiceUnavailable;
            if (!throttled || attempt >= MaxAttempts || request.Content is not null)
                return response;

            var delay = response.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));   // 2,4,8… capped
            response.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}

// -- Graph driveItem JSON (the subset we surface) ------------------------------

internal sealed class DriveItem
{
    // The drive-scoped item id. Stable across renames and moves, and the ONLY field a delta
    // tombstone is guaranteed to carry - which is why the mirror keys on it rather than on a path.
    public string?          Id                   { get; set; }
    public string?          Name                 { get; set; }
    public long?            Size                 { get; set; }
    public DateTimeOffset?  CreatedDateTime      { get; set; }
    public DateTimeOffset?  LastModifiedDateTime { get; set; }
    public string?          ETag                 { get; set; }
    public FileSystemInfoFacet? FileSystemInfo   { get; set; }
    public string?          WebUrl               { get; set; }
    public FileFacet?       File                 { get; set; }
    public FolderFacet?     Folder               { get; set; }
    public DeletedFacet?    Deleted              { get; set; }
    public ParentReference? ParentReference      { get; set; }

    [JsonPropertyName("root")] public object? Root { get; set; }   // present only on the drive root
}

internal sealed class FileFacet
{
    public string?      MimeType { get; set; }
    public HashesFacet? Hashes   { get; set; }
}

// Graph reports content hashes on the file facet. SharePoint and OneDrive for Business return
// quickXorHash only; personal OneDrive returns sha1/sha256 instead. Any of them may be absent.
internal sealed class HashesFacet
{
    public string? QuickXorHash { get; set; }
    public string? Sha1Hash     { get; set; }
    public string? Sha256Hash   { get; set; }
    public string? Crc32Hash    { get; set; }
}

// The client-supplied timestamps of the file as it existed on its originating filesystem.
// Distinct from the driveItem's own created/lastModified, which are service-controlled and move
// for reasons unrelated to the bytes (a column edit, a check-in, a retention label). This facet
// is the one a sync client writes to preserve a file's identity across the copy, and the one
// that round-trips - so it is what the node reports as CreatedAt/ModifiedAt.
internal sealed class FileSystemInfoFacet
{
    public DateTimeOffset? CreatedDateTime      { get; set; }
    public DateTimeOffset? LastModifiedDateTime { get; set; }
}
internal sealed class FolderFacet     { public int?    ChildCount { get; set; } }
internal sealed class DeletedFacet    { public string? State { get; set; } }
internal sealed class ParentReference { public string? DriveId { get; set; } public string? Path { get; set; } }

internal sealed class DriveItemPage
{
    public List<DriveItem>? Value { get; set; }

    [JsonPropertyName("@odata.nextLink")]  public string? NextLink  { get; set; }
    [JsonPropertyName("@odata.deltaLink")] public string? DeltaLink { get; set; }
}

internal sealed class UploadSession
{
    public string? UploadUrl { get; set; }
}

// -- Site / drive resolution (UseSharePointSite) -------------------------------

internal sealed class GraphSite  { public string? Id { get; set; } }
internal sealed class GraphDrive { public string? Id { get; set; } public string? Name { get; set; } }
internal sealed class GraphDriveCollection { public List<GraphDrive>? Value { get; set; } }
