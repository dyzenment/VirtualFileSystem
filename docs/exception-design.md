# Exception design

**Status:** implemented for core and SharePoint. Written 2026-08-26.

- **Done:** the exception surface (`VfsException`, `VfsTransientException`, `VfsFailureReason`,
  `VfsOperation`, `VfsFailureOrigin`, `VfsFailure`); the `IVfsCatalog` implementer obligation as
  documentation; the pipeline net, so no foreign exception type reaches a consumer through
  `IVirtualFileSystem` from **any** node; and the SharePoint mapping, so Graph's failures are
  classified where they still mean something.
- **Not done:** per-node mapping for LocalFs, S3 and Azure. The net already guarantees the *type*
  there - a locked file surfaces as `VfsException` with `Reason = Unknown` rather than `Conflict`,
  so what is missing is accuracy, not conformance.
- **Deferred by agreement:** in-flight failures on a read stream, and the SharePoint mirror
  degradation policy - see [What is still open](#what-is-still-open).

## The problem

The library leaks its backends. `SharePointNode` throws `HttpRequestException` from Graph,
`LocalFsNode` throws `IOException`, an `IVfsCatalog` backed by EF throws `SqlException`, an S3 node
throws whatever the AWS SDK threw. Nothing in the public surface says what went wrong in terms the
library itself defines.

Two consequences:

- **Consumers cannot write mount-agnostic code.** The whole point of a virtual file system is that
  `/sftp/primary` and `/sftp/mirror` are interchangeable to the caller. Today they are — right up
  until something fails, at which point the caller has to know that one is a local disk and the other
  is Graph in order to catch anything meaningful.
- **Classification ends up at a distance.** A consumer that wants to tell "the far end was busy" from
  "the request was wrong" has to reverse-engineer intent from foreign error codes — HTTP status
  numbers, SQL error numbers — in code that has no business knowing them. That is exactly what
  happened downstream in FslSystem: a processor task reading SharePoint status codes and Azure SQL
  error numbers to decide whether to raise an alert.

## The model: wrap, don't hide

Entity Framework is the precedent worth copying. Nobody catches `SqlException` or `OleDbException`
from EF; they catch `DbUpdateException`, and the provider is an implementation detail. But EF does not
*hide* the provider exception — `DbUpdateException.InnerException` is still the `SqlException`. The
common case is provider-agnostic; the rare case that genuinely needs the raw error can still reach it.

VFS holds the same line. A caller who wants to know "was that a 503?" can still find out, but does
not have to in order to write ordinary code.

## The surface

Three types and three enums, and deliberately no more. All in the root
`Dytools.VirtualFileSystem` namespace, under `src/Dytools.VirtualFileSystem/Exceptions/`.

### `VfsException` (base)

Derives from `Exception`, not `IOException`. Deriving from `IOException` would have left existing
`catch (IOException)` blocks silently working, which is precisely the coupling this change exists to
break — a caller should have to notice.

Carries where it happened, in the library's own terms:

- `Mount` — the mount the failure came from, or null outside the pipeline.
- `Path` — the full, mount-qualified VFS path, where there is one.
- `Operation` — a `VfsOperation`.
- `Reason` — a `VfsFailureReason`.
- `Origin` — a `VfsFailureOrigin`.
- `InnerException` — the backend's own exception, always preserved.

`Mount` and `Path` are materialised `string`s rather than `VfsPath`. A `VfsPath` is a slice over a
buffer it does not own; an exception outlives the call that made it and gets logged and serialised,
so it should not hold one.

The generated message reads
`VFS Read failed on '/sp/docs/folder/report.pdf' (AccessDenied). See the inner exception (HttpRequestException) for details.`
— reason and location in the line a log actually prints, without needing a full `ToString()`.

### `VfsTransientException : VfsException`

The far end could not service the request, as opposed to the request being wrong. Adds:

- `RetryAfter` (nullable) — Graph sends this with throttling responses, and ignoring it is how
  throttling becomes more throttling.

### `VfsFailureReason` — a closed enum

`Unknown`, `NotFound`, `AccessDenied`, `Conflict`, `Throttled`, `Unavailable`, `Timeout`,
`InvalidPath`, `QuotaExceeded`.

The listable, documentable set a consumer can switch on without knowing the backend.

**`Unknown` is the zero value.** `default(VfsFailureReason)` has to mean "unclassified, and therefore
real" — if `NotFound` sat at zero, a forgotten argument would invent a specific meaning nobody
established. A node that hits something it cannot map throws with `Reason = Unknown` and the inner
exception intact, failing toward "tell me" rather than "swallow".

`NotFound` is not raised on the ordinary "is it there?" paths: `OpenReadAsync` and `GetInfoAsync`
still return `null` for a missing entry. It is for operations where absence is a failure, such as
copying from a source that is not there.

### `VfsOperation` and `VfsFailureOrigin`

`VfsOperation` mirrors the `IVfsNode` surface — `Read`, `Write`, `Delete`, `Copy`, `Move`, `Rename`,
`List`, `Exists`, `GetInfo` — plus `Sync` (a change-feed / delta run) and `Resolve` (a backend's own
addressing, such as a SharePoint site and drive id, which can fail before any entry operation
begins). `Unknown` is the zero value.

`VfsFailureOrigin` is `Backend` or `Catalog`. **Where** a failure came from is a property, not a
subclass: making it a type would mean a class per combination of category and location
(`VfsTransientCatalogException` and the rest) for a distinction most callers never make. The callers
that do make it — a caching node deciding whether it can degrade to serving the backend directly —
only need to read a bit.

### Only transient is positively identified

There is no `VfsRealException`. Transient is the only category a node has to positively recognise;
everything else is real by default. That is the safe direction — an unrecognised failure surfaces
rather than being quietly tolerated — and it keeps the hierarchy to one type instead of a cross
product of category and location.

Consumers catching both must order the arms with `VfsTransientException` first. It derives from the
base, so a leading `catch (VfsException)` silently collapses the two.

## `VfsFailure` — the boundary helper

A public static that decides what is even eligible to become a `VfsException`, and builds them.
The canonical call-site shape uses an exception *filter*, so anything ineligible is never caught in
the first place — no `throw;`, no stack rewriting, no accidental swallowing:

```csharp
try { /* backend call */ }
catch (Exception ex) when (VfsFailure.ShouldWrap(ex, ct))
{
    throw VfsFailure.Wrap(ex, VfsOperation.Read, request);
}
```

- `ShouldWrap(ex, ct)` — the filter predicate. The token is not optional; it is what separates a
  cancel from a timeout.
- `Wrap(ex, operation, …)` — the fallback for what a node did not recognise. Classifies only what is
  identifiable without backend knowledge (a timeout); everything else becomes `Unknown`, and
  therefore real.
- `Create(reason, operation, request, …)` / `Transient(reason, operation, request, …)` — for a node
  that *can* classify its own backend, which should do so at the call site rather than throwing raw
  and letting `Wrap` guess. Both take a `VfsNodeRequest`, so a throw site is one line instead of six
  lines of path reconstruction.

### What passes through untouched

- **An existing `VfsException`.** Re-wrapping buries the reason a node worked out.
- **Genuine cancellation** — see below.
- **API-misuse exceptions**: `ArgumentException`, `NotSupportedException`, `NotImplementedException`,
  `ObjectDisposedException`. They say the caller called wrong, not that storage failed, and dressing
  them as storage failures hides bugs behind a retry. EF holds the same line: it wraps provider
  failures and lets `ArgumentNullException` through.
- **`OutOfMemoryException`**, which nothing should be catching.

`ObjectDisposedException` is excluded *by exact type* because it derives from
`InvalidOperationException`, which **is** wrapped — a backend client throwing an
`InvalidOperationException` is reporting a broken connection, not an API misuse. Every
`InvalidOperationException` the library itself throws comes from mount configuration or builder
setup, which happens before the net is reached.

## Cancellation is decided by the token, not the type

`OperationCanceledException` / `TaskCanceledException` pass through untouched **when the caller's
token asked for it**. Wrapping a real cancel breaks every `catch (OperationCanceledException)` in the
ecosystem, including the cooperative-cancellation contract callers rely on.

But a type check alone is wrong, and dangerously so. `HttpClient` reports its *own* timeout as a
`TaskCanceledException` — with a `TimeoutException` inner and the caller's token never signalled. Let
that through as cancellation and every Graph timeout lands in the one category a consumer is most
likely to treat as expected and never alert on. So:

```csharp
if (ex is OperationCanceledException) return !ct.IsCancellationRequested;
```

A cancellation the caller did not ask for is a failure, and becomes
`VfsTransientException` with `Reason = Timeout`. The race — the caller cancelling at the same instant
a timeout fires — resolves toward "cancelled", which is the harmless direction.

## The three categories

Transient, real, cancelled. That is the whole taxonomy, and it maps onto a consumer switch directly:

```csharp
catch (OperationCanceledException) { /* the caller or a watchdog stopped us */ }
catch (VfsTransientException)      { /* the far end was busy; retry is reasonable */ }
catch (VfsException)               { /* real; repeating it unchanged fails the same way */ }
```

## Per-node mapping

Each node maps its own backend at the boundary, where it still knows what the error meant.

**SharePoint (Graph).** 408, 429, 502, 503, 504 → `VfsTransientException`; `Throttled` for 429,
`Unavailable` otherwise, `Timeout` for 408. A request that never got an answer (null status,
`SocketException`) → transient, `Unavailable`. 404 → `NotFound`. 401/403 → `AccessDenied`. 409 →
`Conflict`. 507 → `QuotaExceeded`. **500 is deliberately not transient** — an opaque server error can
just as easily be something we sent. Honour `Retry-After` into `RetryAfter`.

**LocalFs.** `DirectoryNotFoundException`/`FileNotFoundException` → `NotFound`.
`UnauthorizedAccessException` → `AccessDenied`. `IOException` for a sharing violation or a locked
file → transient, `Unavailable` (the file is likely still being written). Disk full → `QuotaExceeded`.

**Azure / S3.** Map the SDK's own retryable/throttling classification onto transient; 404 →
`NotFound`; 403 → `AccessDenied`. Both SDKs already expose this — do not re-derive it from status
codes.

## The two layers

**Layer 1, the node boundary — classification.** Only the node knows a 429 from a 409. This is where
`Create` and `Transient` are called, and for SharePoint it is two distinct seams: replacing
`EnsureSuccessStatusCode()` with a status-mapping check (so `HttpRequestException` is never born),
and a filter for what happens *before* a response exists — a dead socket, a DNS failure, a timeout.

For SharePoint that is `GraphErrors`: `EnsureOkAsync` replaces every `EnsureSuccessStatusCode()`, so
an `HttpRequestException` is never born and the status is read while it still means something;
`Transport` handles the calls that never got an answer. The failure message carries Graph\'s own error
code, which is usually the most useful thing in the whole exception:

```
VFS GetInfo failed on '/sp/docs/report.pdf' (AccessDenied).
Graph returned 403 Forbidden (accessDenied): Access denied to the requested resource.
```

**Layer 2, the pipeline net — the guarantee.** A filter around each chain in `VfsPipeline` turning
anything unrecognised into `VfsException(Unknown)` with the inner intact. This makes the contract
true for LocalFs, S3, Azure and any consumer-written node before any of them are individually mapped.
Per-node mapping then upgrades *accuracy*, not *conformance*.

The net sits at the pipeline, not at `DefaultVirtualFileSystem`, for two reasons: `VfsContext`
already carries the mount and full path, and it draws a defensible line — the net covers backend
execution, not path parsing or mount resolution, which fail before a chain is entered and stay
`InvalidOperationException`/`ArgumentException` because they are configuration errors.

## What is still open

A wrapper around a method only covers what that method does before it returns, and three things
happen outside one.

- **`OpenReadAsync` hands back a live backend stream.** A connection dropped mid-read throws raw,
  outside any node method — and on a large file that is exactly the transient case that matters most.
  It needs a mapping `Stream` decorator. *Deferred, and the one real hole in the contract.*
- **Listing is an async iterator** — covered. The pipeline drives the enumerator with each
  `MoveNextAsync` inside the filter, and `SharePointNode` does the same for its own paging so a 429
  partway through a large library is transient rather than `Unknown`. Nothing is buffered: entries
  already produced still reach the caller before the failure does. C# forbids `yield return` inside a
  `try` that has a `catch`, so every one of these fetches in the `try` and yields outside it.
- **A write commits on close** — covered. `SharePointUploadStream` uploads from `DisposeAsync`, long
  after `OpenWriteAsync` returned, so it carries the VFS path with it and `CommitUploadAsync` maps
  its own failures. A caller sees the `VfsException` from the `await using`.

Two smaller gaps, both deliberate:

- **`SyncAsync`\'s own mirror writes are not tagged `Origin.Catalog`.** The delta loop is exactly
  what the mirror-degradation work restructures — cursor checkpointing, the lease, the per-write
  timeout — so tagging those sites now would be work that pass immediately redoes. Failures there
  still surface, as `Sync` with `Origin.Backend`.
- **Paths that go through `GetFromJsonAsync`** (listing pages, delta pages, drive resolution) are
  classified by the status carried on the `HttpRequestException`, which is correct, but lose
  `Retry-After`. In practice `ThrottleRetryHandler` has already honoured it and retried four times
  before anything escapes.

## The catalog is the one boundary VFS does not own

`IVfsCatalog` is an interface the *consumer* implements. FslSystem's implementation is EF-backed and
lives in FslSystem; the library has no SQL dependency and should not gain one. So VFS cannot classify
a `SqlException` itself.

Two halves:

- **Documented obligation on implementers** (now on the `IVfsCatalog` XML docs): signal transient
  storage failures by throwing `VfsTransientException`, and let `OperationCanceledException`
  propagate untouched. An implementation that does not is simply treated as real, which is the safe
  default.
- **VFS wraps whatever escapes a catalog call** into `VfsException` with `Origin = Catalog` and the
  inner preserved, so a consumer still gets a library-shaped exception rather than an `SqlException`
  leaking through a `SharePointNode.GetInfoAsync` call — which is precisely the shape that caused the
  production incident this note came out of.

## Consequences

This is a **breaking change** for anyone catching backend exception types today. The package is at
0.8.0, so pre-1.0 is the right moment — this is the kind of contract that should be settled before
1.0, not after.

It also removes the reason for consumer-side classification helpers that read foreign error codes.
Downstream, `SharePointAvailability`'s HTTP knowledge moves into the SharePoint node, and
`DatabaseAvailability` moves to exactly one caller — the catalog implementation, which is the only
thing that knows what SQL error numbers mean.

## Settled

- **`Reason` is a closed enum**, not an extensible struct wrapper. Easier to switch on and easier to
  document, with `Unknown` as the escape hatch.
- **Mid-stream failures do get annotated**, since the path is known at open time — but via a stream
  decorator, not the pipeline net, and that is deferred.
- **`IChangeFeed` gets no reason values of its own** for now. A cursor the backend no longer accepts
  is neither transient nor really `Conflict`; `Unknown` covers it until it comes up in practice.
