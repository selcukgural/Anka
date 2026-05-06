# AI Agent Guidelines — Anka

**Anka** is a zero-allocation HTTP/1.x server library for .NET 8+ Native AOT. Designed for sub-25 ms cold starts with no heap allocation in steady state.

---

## Project Overview

- **Purpose:** Minimal HTTP server for serverless / edge computing with near-instant cold starts
- **Architecture:** Single-pass parser → pooled buffers → zero-allocation hot paths
- **Status:** Beta — research/experimentation only (not production-ready)
- **Key Trade-offs:** No middleware, no routing, no HTTP/2, no TLS — user code handles dispatch

**Critical insight:** This project prioritizes memory efficiency and cold-start performance above all else. Allocations are tracked obsessively, every buffer is pooled, and async state machines are crafted manually to avoid captures.

---

## Essential Architecture

### Request Lifecycle (One Per Keep-Alive Connection)

```
1. SocketReceiver.ReceiveAsync()     ← OS notifies; synchronous on loopback, async on WAN
2. HttpParser.TryParse()             ← Single-pass parse into pooled HttpRequest
3. ← (Expect: 100-continue if needed)
4. Connection.ReadBodyAsync()        ← Transfer-Encoding: chunked or Content-Length
5. RequestHandler(request, response) ← User delegate
6. HttpResponseWriter.WriteAsync()   ← Direct to socket
7. Keep-alive check                  ← Loop back to step 1, or close
```

### Buffer Management (Memory Model)

**Per-connection allocation (once, reused):**
- `ArrayPool.Rent(64 KB)` — sliding receive window, compacted only when tail is full
- `HttpRequestPool.Rent()` — single-slot CAS pool (not `ConcurrentQueue` — avoid ~608 B allocations)
- `HttpResponseWriter.buffer` — `ArrayPool` rented, returned on dispose

**Per-request allocation (zero on existing connections):**
- HttpRequest: reused via `ResetForReuse()`
- Headers: inline 64-element array in struct (no heap)
- Body: read into rented buffer, then `ReadOnlyMemory<byte>` slice

**Connection close:**
- `HttpRequest.Dispose()` returns buffers to `ArrayPool`
- `SocketReceiver.Dispose()` releases `SocketAsyncEventArgs`

**Result:** Steady state is **zero allocation**.

### Core Data Flow

```
TCP bytes
  ↓ SocketReceiver (SocketAsyncEventArgs + IValueTaskSource)
  ↓ 64 KB pooled receive buffer (sliding window)
  ↓ HttpParser.TryParse(ref SequenceReader) — single pass
  ↓ Parse request-line (method, target, version)
  ↓ Parse headers (lowercase names, duplicate-aware)
  ↓ Extract & validate Content-Length inline
  ↓ 100-continue sent if Expect header present
  ↓ Read body (Content-Length or chunked)
  ↓ RequestHandler delegate
  ↓ HttpResponseWriter.WriteAsync() (body omitted for HEAD/304)
  ↓ Socket.SendAsync()
```

---

## Key Directories & Modules

| Directory | Files | Purpose |
|-----------|-------|---------|
| `src/Anka/src/Core/` | `Server.cs`, `HttpRequest.cs`, `HttpResponseWriter.cs`, `HttpHeaders.cs`, `HttpHeaderNames.cs`, `HttpMethod.cs`, `HttpVersion.cs`, `ServerOptions.cs` | Public API; everything a user imports |
| `src/Anka/src/Internal/` | `Connection.cs`, `HttpParser.cs`, `HttpRequestPool.cs`, `SocketReceiver.cs`, `ChunkedBodyParser.cs`, `HttpMethodParser.cs`, `HttpVersionParser.cs` | Implementation; hidden from users; tested via `InternalsVisibleTo` |
| `src/Anka/src/Exceptions/` | `AnkaArgumentException.cs`, `AnkaOutOfRangeException.cs` | Domain-specific exception types |
| `Test/Anka.Test/` | 242 unit tests across 13 files | Full parser, transport, limits, validation coverage |
| `Benchmark/Anka.Benchmark/` | `HttpParserBenchmarks.cs`, etc. | BenchmarkDotNet micro-benchmarks (target: zero allocation) |

---

## Critical Conventions

### 1. UTF-8Literals & Zero-Copy Everywhere

```csharp
// ✓ Correct — no allocation on hot paths
var body = "Hello"u8.ToArray();
await res.WriteAsync(200, body, "text/plain"u8.ToArray(), ct);

// ✗ Avoid on hot paths — allocates every call
await res.WriteAsync(200, Encoding.UTF8.GetBytes("Hello"), ...);
```

**Why:** AOT binaries cannot rely on JIT's string interning. Literal strings must be bytes.

### 2. HttpHeaderNames Are Properties, Not Fields

```csharp
// ✓ Correct — AOT-safe
public static ReadOnlySpan<byte> Host => "host"u8;

// ✗ WRONG — ReadOnlySpan<byte> cannot be stored as static field
public static readonly ReadOnlySpan<byte> Host = "host"u8;
```

**Why:** `ReadOnlySpan<byte>` is a ref struct; it cannot live in fields. Must be returned from properties each call (zero-cost due to inlining).

### 3. Ref Struct & Async State Machine Boundary

```csharp
// ✓ Correct — ref struct stays out of async state machine
if (!TryParseNext(buf, offset, length, req, out consumed)) break;

private static bool TryParseNext(byte[] buf, int offset, int len, HttpRequest req, out int consumed)
{
    var seq = new ReadOnlySequence<byte>(buf, offset, len);
    var reader = new SequenceReader<byte>(seq);  // ref struct — local only
    bool ok = HttpParser.TryParse(ref reader, req);
    consumed = ok ? (int)reader.Consumed : 0;
    return ok;
}

// ✗ WRONG — would box ref struct into async state machine
return HttpParser.TryParse(ref reader, req);  // from async context
```

**Why:** `SequenceReader<byte>` is a ref struct. If captured in async state machine, the compiler will box it, breaking AOT.

### 4. Header Array Allocation — Once at Startup

```csharp
// ✓ Pre-allocate once at startup for zero per-request cost
private static readonly HttpHeader[] _corsHeaders =
[
    new("access-control-allow-origin"u8.ToArray(), "*"u8.ToArray()),
    new("access-control-allow-methods"u8.ToArray(), "GET, POST"u8.ToArray()),
];

// In handler:
await res.WriteAsync(200, body, contentType, true, _corsHeaders, ct);

// ✗ Avoid — allocates List per call (acceptable for low-frequency paths only)
await res.AddHeader(...).AddHeader(...).WriteAsync(...);
```

### 5. Keep-Alive Semantics

- **HTTP/1.1:** keep-alive ON by default; off only on `Connection: close`
- **HTTP/1.0:** keep-alive OFF by default; on only on `Connection: keep-alive`
- `HttpRequest.IsKeepAlive` is computed during parsing by `HttpParser.ComputeKeepAlive`
- Always pass `request.IsKeepAlive` to `WriteAsync` to mirror client preference

```csharp
await res.WriteAsync(status, body, contentType, keepAlive: request.IsKeepAlive, ct);
```

---

## Build & Test Commands

```bash
# Build (all projects)
dotnet build Anka.slnx --nologo

# Full test suite (242 tests)
dotnet test Anka.slnx --nologo

# Single test by name
dotnet test Test/Anka.Test --nologo --filter "FullyQualifiedName~TryParse_SimpleGet_ReturnsRequest"

# Microbenchmarks (BenchmarkDotNet)
dotnet run --project Benchmark/Anka.Benchmark -c Release

# Load testing (startup + throughput)
dotnet run --project Test/LoadTest/Anka.Wrk.LoadTest --configuration Release
```

---

## Testing Patterns

### Parser Unit Tests (No Server Needed)

```csharp
var bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
var seq = new ReadOnlySequence<byte>(bytes);
var reader = new SequenceReader<byte>(seq);
var req = new HttpRequest();
Assert.True(HttpParser.TryParse(ref reader, req) == HttpParseResult.Success);
req.Return();  // Always return to pool!
```

### Integration Tests (With Server)

```csharp
await using var server = await TestServer.StartAsync(
    static (req, res, ct) => res.WriteAsync(200, body, contentType, req.IsKeepAlive, ct));

using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, server.Port);
// Send raw HTTP bytes, read response
```

**Critical:** Always call `req.Return()` or `req.Dispose()` to avoid pool exhaustion in tests.

---

## Common Pitfalls

1. **String allocation on hot paths** — Use `"..."u8` bytes literals instead
2. **Storing `ReadOnlySpan<byte>` in fields** — Must be property-backed for AOT
3. **Calling async from sync context** — Wrap ref struct access in synchronous helper
4. **Forgetting `req.Return()`** — Exhausts the single-slot pool; leaves garbage for GC
5. **`ConcurrentQueue<T>` instead of CAS pool** — Allocates ~608 B per 32 operations
6. **Trying to optimize streaming response bodies** — Use `response.GetStream()` for chunked transfer encoding (zero-allocation for header construction).
7. **Assuming chunked response encoding works** — Fully supported via `response.GetStream()`.Terminating chunk and trailers managed automatically.

---

## Extension Points

### Adding a New HTTP Header Constant

1. Add property to `HttpHeaderNames.cs`:
   ```csharp
   public static ReadOnlySpan<byte> XCustom => "x-custom"u8;
   ```

### Adding a New Internal Parser Component

1. Create file in `src/Anka/src/Internal/`
2. Mark as `internal class`
3. Tests can access via `InternalsVisibleTo` — no `public` needed

### Modifying HTTP Parsing

1. Edit `src/Anka/src/Internal/HttpParser.cs` (single-pass parse logic)
2. Add test case to `Test/Anka.Test/HttpParserTests.cs`
3. Run: `dotnet test Test/Anka.Test --nologo --filter "...test name..."`

---

## Performance Targets

| Metric | Target | Current |
|--------|--------|---------|
| Cold start (ready) | < 25 ms | 2.3 ms |
| Steady-state allocation | 0 B per request | 0 B |
| Startup allocation | < 1 MB | 124.5 KB |
| Parser throughput | > 90 ns / simple req | 92.9 ns |
| Keep-alive req throughput | ~130k req/s (loopback) | 133k req/s |

**Benchmark suite is expected to show 0 allocated bytes.** If a benchmark allocates, investigate immediately.

---

## Async Safety Rules

1. **SocketReceiver.ReceiveAsync()** — ValueTask-based; completes synchronously on loopback
   - No thread switch = no allocation on fast path
   - WAN traffic posts to ThreadPool via `IValueTaskSource.OnCompleted`

2. **Connection.ProcessAsync()** — Single task per connection
   - Manage cancellation via `_cancellationToken.Register(socket.Close, ...)`
   - Use read timeout `CancellationTokenSource` for Slowloris protection

3. **RequestHandler** — User delegate is awaited
   - Can be `static` (preferred for AOT)
   - Receives pooled `HttpRequest` — valid only during the call

---

## Document References

- **Architecture:** Lines 393–426 of README (box diagram + data flow)
- **RFC Compliance:** Lines 324–390 of README (supported/unsupported features)
- **Performance Targets:** Lines 855–970 of README (benchmarks + end-to-end results)
- **Test Coverage:** Lines 1045–1067 of README (242 tests across 13 suites)

---

## When to Make Changes

| Scenario | Action |
|----------|--------|
| Add a feature that allocates | Discuss; likely needs architectural change |
| Add validation to parser | Update `HttpParser.cs` + test in `HttpParserTests.cs` |
| Fix a limit enforcement | Update `ServerOptions` property + test in `RequestBodySizeLimitTests.cs` etc. |
| Add HTTP status code | Update `HttpResponseWriter.GetStatusString()` |
| Support new request-target form | Update `RequestTargetForm.cs` enum + parser logic |
| Performance regression | Check benchmarks: `dotnet run --project Benchmark/Anka.Benchmark -c Release` |


