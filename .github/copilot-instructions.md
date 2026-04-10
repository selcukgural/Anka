# Copilot Instructions — Anka

Minimal zero-allocation HTTP/1.x server library for .NET 8+, designed for Native AOT.
Single `RequestHandler` delegate — no middleware pipeline, no built-in routing.

---

## Commands

```bash
# Build
dotnet build Anka.slnx --nologo

# Test (full suite)
dotnet test Anka.slnx --nologo

# Single test
dotnet test Test/Anka.Test --nologo --filter "FullyQualifiedName~TryParse_SimpleGet_ReturnsRequest"
```

---

## Architecture

```
Server.StartAsync()
  └─ AcceptLoopAsync()           multiple parallel accept loops
       └─ Connection.RunAsync()  one Task per TCP connection
            ├─ SocketReceiver    SocketAsyncEventArgs + IValueTaskSource (zero alloc receive)
            ├─ HttpParser.TryParse()  single-pass parse into pooled HttpRequest
            ├─ RequestHandler(request, writer, ct)  user delegate
            └─ HttpResponseWriter.WriteAsync()  writes directly to socket
```

**Request lifecycle:**
1. `Connection` rents an `HttpRequest` from `HttpRequestPool` (CAS lock-free pool, 32 slots).
2. `HttpParser.TryParse` fills it in one pass — path/query/headers share a single rented buffer; Content-Length is extracted inline.
3. The `RequestHandler` delegate is awaited.
4. `HttpRequest.ResetForReuse()` keeps the buffer for the next request on the same keep-alive connection.
5. `HttpRequest.Dispose()` (called by `Connection` on close) returns all rented buffers to `ArrayPool<byte>.Shared`.

**Directory layout:**
- `src/Anka/src/Core/` — public API (`HttpRequest`, `HttpResponseWriter`, `HttpHeaders`, `HttpHeader`, `HttpHeaderNames`, `HttpMethod`, `HttpVersion`, `ServerOptions`, `RequestHandler`, `ResponseContext`, `HttpResponseWriterExtensions`)
- `src/Anka/src/Internal/` — implementation details (`Connection`, `HttpParser`, `HttpRequestPool`, `SocketReceiver`, `HttpMethodParser`, `HttpVersionParser`)
- `src/Anka/src/Exceptions/` — `AnkaArgumentException`, `AnkaPortOutOfRageException`
- `Test/Anka.Test/` — xUnit tests (accesses internals via `InternalsVisibleTo`)

---

## Key Conventions

### UTF-8 literals everywhere
Use `"..."u8` literals and `ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>` throughout. Avoid `string` on hot paths.

```csharp
// Correct — zero allocation
var body = "Hello"u8.ToArray();
await res.WriteAsync(200, body, "text/plain"u8.ToArray(), cancellationToken: ct);

// Avoid on hot paths
await res.WriteAsync(200, Encoding.UTF8.GetBytes("Hello"), ...);
```

### HttpHeaderNames are ReadOnlySpan<byte> properties (not fields)
They must be properties (not static fields) to be AOT-safe — `ReadOnlySpan<byte>` cannot be stored as a field.

```csharp
// Correct — property
public static ReadOnlySpan<byte> Host => "host"u8;

// WRONG — won't compile for AOT
public static readonly ReadOnlySpan<byte> Host = "host"u8; // invalid
```

### HttpHeader — allocate once at startup
`HttpHeader` stores `ReadOnlyMemory<byte>`. For zero-allocation hot paths, create instances as `static readonly` arrays:

```csharp
private static readonly HttpHeader[] _corsHeaders =
[
    new("access-control-allow-origin"u8.ToArray(), "*"u8.ToArray()),
];

await res.WriteAsync(200, body, contentType, true, _corsHeaders, ct);
```

### HttpResponseWriter.WriteAsync — contentType is bytes, not string
Both `body` and `contentType` parameters are `ReadOnlyMemory<byte>`:

```csharp
// Correct
await res.WriteAsync(200, body, "application/json"u8.ToArray(), cancellationToken: ct);

// Fluent extra headers (allocates a List — use for low-frequency paths only)
await res.AddHeader(HttpHeaderNames.Location, "/new"u8)
         .WriteAsync(301, default, default, keepAlive: false, ct);
```

### HttpParser — ref struct kept out of async state machine
`SequenceReader<byte>` is a ref struct. The `TryParseNext` static wrapper in `Connection` exists solely to prevent the ref struct from being captured in the async state machine. Follow this pattern when calling `HttpParser.TryParse` from async code:

```csharp
// Call synchronous wrapper from the async loop
if (!TryParseNext(buf, offset, length, request, out var consumed)) break;

private static bool TryParseNext(byte[] buf, int offset, int length, HttpRequest req, out int consumed)
{
    var seq = new ReadOnlySequence<byte>(buf, offset, length);
    var reader = new SequenceReader<byte>(seq);
    if (HttpParser.TryParse(ref reader, req)) { consumed = (int)reader.Consumed; return true; }
    consumed = 0; return false;
}
```

### Adding new internal types
Place in `src/Anka/src/Internal/`, namespace `Anka`, mark `internal`. Tests can access them directly because `Anka.csproj` has:
```xml
<InternalsVisibleTo>Anka.Test</InternalsVisibleTo>
<InternalsVisibleTo>Anka.Benchmark</InternalsVisibleTo>
```

### Chunked transfer encoding
Chunked request bodies are currently rejected with `501`. Chunked response encoding is not implemented; all responses carry `Content-Length`.

### Keep-alive semantics
- HTTP/1.1: keep-alive **on** by default; off only when `Connection: close`.
- HTTP/1.0: keep-alive **off** by default; on when `Connection: keep-alive`.
- `HttpRequest.IsKeepAlive` is computed by `HttpParser.ComputeKeepAlive` during parsing.
- The connection loop exits after the response when `keepAlive == false`. Pass `request.IsKeepAlive` to `WriteAsync` to mirror the request's preference.

---

## Testing Patterns

Integration tests use the `TestServer` helper (defined in `TransportTests.cs` / `CustomResponseHeaderTests.cs`):

```csharp
await using var server = await TestServer.StartAsync(
    static (req, res, ct) => res.WriteAsync(200, body, contentType, req.IsKeepAlive, ct));

using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, server.Port);
```

`HttpParser` unit tests call it directly — no server needed:

```csharp
var bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
var seq = new ReadOnlySequence<byte>(bytes);
var reader = new SequenceReader<byte>(seq);
var req = new HttpRequest();
Assert.True(HttpParser.TryParse(ref reader, req));
req.Return();
```

Always call `req.Return()` (or `req.Dispose()`) in tests to avoid pool exhaustion.
