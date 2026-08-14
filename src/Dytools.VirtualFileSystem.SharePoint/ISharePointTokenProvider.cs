namespace Dytools.VirtualFileSystem.Nodes.SharePoint;

// The one thing you implement to plug SharePoint into your app: hand back a current Microsoft
// Graph access token. Your credential system owns acquisition, refresh, and scopes - the node
// just asks for the token in force right now and attaches it as a bearer at the transport
// boundary. It never stores, refreshes, or logs the token.
//
// The token must already carry the Graph permissions the operations need (e.g.
// Files.ReadWrite.All / Sites.ReadWrite.All, app-only or delegated as your system does it).
//
// Register your implementation in DI; the node resolves it:
//   services.AddSingleton<ISharePointTokenProvider, MyCredentialBridge>();
public interface ISharePointTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken ct = default);
}
