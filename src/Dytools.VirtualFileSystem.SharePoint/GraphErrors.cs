using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// Maps Microsoft Graph's failures onto the VFS exception contract, at the one place that still
// knows what they meant. Two seams, because Graph fails in two shapes:
//
//   EnsureOkAsync - a response arrived and said no. Replaces EnsureSuccessStatusCode(), so an
//                   HttpRequestException is never born in the first place.
//   Transport     - no response arrived: a dead socket, DNS, TLS, an HttpClient timeout. Also
//                   catches an HttpRequestException that some other helper (GetFromJsonAsync)
//                   already made out of a status, and classifies it by that status rather than
//                   assuming the network was at fault.
internal static class GraphErrors
{
    private const int MaxErrorBody = 512;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Throws a mapped VfsException when the response is not a success. Returns the response
    // otherwise, so it can stand in for EnsureSuccessStatusCode() in a chain.
    public static async Task<HttpResponseMessage> EnsureOkAsync(
        this HttpResponseMessage resp, VfsOperation op, string? path, string? mount, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return resp;
        throw await FromResponseAsync(resp, op, path, mount, ct).ConfigureAwait(false);
    }

    public static async Task<VfsException> FromResponseAsync(
        HttpResponseMessage resp, VfsOperation op, string? path, string? mount, CancellationToken ct)
    {
        var (reason, transient) = Classify((int)resp.StatusCode);
        var detail = await ReadErrorAsync(resp, ct).ConfigureAwait(false);
        var message = Message(op, path, reason, (int)resp.StatusCode, resp.ReasonPhrase, detail);

        return transient
            ? new VfsTransientException(reason, op, path, mount, message, retryAfter: RetryAfter(resp))
            : new VfsException(reason, op, path, mount, message);
    }

    // No response, or one another helper already turned into an exception.
    public static VfsException Transport(
        Exception ex, VfsOperation op, string? path, string? mount, CancellationToken ct)
    {
        // GetFromJsonAsync and friends call EnsureSuccessStatusCode themselves, so the status is
        // still on the exception. Classify by it - assuming "the network was down" for what was
        // really a 404 would turn a permanent failure into one that retries forever.
        if (ex is HttpRequestException { StatusCode: { } status })
        {
            var (statusReason, statusTransient) = Classify((int)status);
            var statusMessage = Message(op, path, statusReason, (int)status, null, "");
            return statusTransient
                ? new VfsTransientException(statusReason, op, path, mount, statusMessage, ex)
                : new VfsException(statusReason, op, path, mount, statusMessage, ex);
        }

        // Everything below never reached a server, or lost the connection partway.
        var reason = ex switch
        {
            // Reaching here through VfsFailure.ShouldWrap, a cancellation is one the caller did not
            // ask for - an HttpClient timeout wearing TaskCanceledException's clothes.
            OperationCanceledException or TimeoutException => VfsFailureReason.Timeout,
            HttpRequestException or SocketException or IOException => VfsFailureReason.Unavailable,
            _ => VfsFailureReason.Unknown,
        };

        // A response Graph never sent cannot be blamed on the request, so those are transient.
        // Anything else - a malformed body, a bug - stays real.
        return reason == VfsFailureReason.Unknown
            ? new VfsException(reason, op, path, mount, innerException: ex)
            : new VfsTransientException(reason, op, path, mount, innerException: ex);
    }

    // -- The status table -------------------------------------------------------

    // 500 is deliberately absent from the transient set. An opaque server error can just as easily
    // be something we sent, and retrying a bad request forever is worse than surfacing it once.
    private static (VfsFailureReason Reason, bool Transient) Classify(int status) => status switch
    {
        408 => (VfsFailureReason.Timeout,       true),
        429 => (VfsFailureReason.Throttled,     true),
        502 or 503 or 504
            => (VfsFailureReason.Unavailable,   true),

        404 => (VfsFailureReason.NotFound,      false),
        401 or 403
            => (VfsFailureReason.AccessDenied,  false),
        409 => (VfsFailureReason.Conflict,      false),
        507 => (VfsFailureReason.QuotaExceeded, false),

        _   => (VfsFailureReason.Unknown,       false),
    };

    // Graph sends Retry-After as a delay on throttling responses, and occasionally as a date.
    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var header = resp.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date  is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    // -- Message ---------------------------------------------------------------
    //
    // Same shape VfsException generates, with what Graph actually said appended - the status and
    // its error code are the difference between a log line worth reading and one worth ignoring.

    private static string Message(
        VfsOperation op, string? path, VfsFailureReason reason, int status, string? phrase, string detail)
    {
        var where  = string.IsNullOrEmpty(path) ? "" : $" on '{path}'";
        var reason_ = string.IsNullOrEmpty(phrase) ? $"{status}" : $"{status} {phrase}";
        return $"VFS {op} failed{where} ({reason}). Graph returned {reason_}{detail}.";
    }

    // Graph reports failures as {"error":{"code":…,"message":…}}, and the code is usually the most
    // useful thing in the whole exception. Diagnostics only: a failure to read it must never
    // replace the failure it was describing, so everything here is swallowed.
    private static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length == 0) return "";

            if (JsonSerializer.Deserialize<GraphErrorEnvelope>(body, Json)?.Error is { Code.Length: > 0 } e)
                return $" ({e.Code}): {Truncate(e.Message)}";

            return $": {Truncate(body)}";
        }
        catch
        {
            return "";
        }
    }

    private static string? Truncate(string? s)
        => s is { Length: > MaxErrorBody } ? s[..MaxErrorBody] + "…" : s;
}

internal sealed class GraphErrorEnvelope
{
    [JsonPropertyName("error")] public GraphErrorBody? Error { get; set; }
}

internal sealed class GraphErrorBody
{
    public string? Code    { get; set; }
    public string? Message { get; set; }
}
