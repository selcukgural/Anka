# Anka

> **Version: v0.0.1**

Anka is a minimal HTTP/1.x server library for .NET 8+, written from scratch using raw sockets and `System.IO.Pipelines`, with a **zero-allocation** design. It is fully compatible with AOT (Ahead-of-Time) compilation.

---

## Table of Contents

1. [Features and Limitations](#features-and-limitations)
2. [Quick Start](#quick-start)
3. [Architecture Overview](#architecture-overview)
4. [Application Lifecycle](#application-lifecycle) — step by step from start to finish
5. [Class Reference — Public API](#class-reference--public-api)
6. [Class Reference — Internal](#class-reference--internal)
7. [Memory Model](#memory-model)
8. [Performance Profile](#performance-profile)
9. [Project Structure](#project-structure)

---

## Features and Limitations

### ✅ Supported

| Area                  | Detail                                                                                                                     |
|-----------------------|----------------------------------------------------------------------------------------------------------------------------|
| **HTTP Versions**     | HTTP/1.0, HTTP/1.1                                                                                                         |
| **HTTP Methods**      | GET, POST, PUT, DELETE, HEAD, OPTIONS, PATCH, TRACE, CONNECT                                                               |
| **Keep-Alive**        | HTTP/1.1 on by default; disabled with `Connection: close`. HTTP/1.0 off by default; enabled with `Connection: keep-alive`. |
| **Request Body**      | Body specified by `Content-Length` is read in full                                                                         |
| **Request Headers**   | Up to 64 headers; names are normalised to lowercase                                                                        |
| **Response**          | Status code, body, Content-Type, Content-Length, Connection header                                                         |
| **Response Body**     | ≤ 4096 bytes: inlined into the header buffer (single `SendAsync`). > 4096 bytes: separate `SendAsync`                      |
| **Concurrency**       | Each connection runs as an independent `Task`; the accept loop never waits on any connection                               |
| **Backpressure**      | `PipeOptions.pauseWriterThreshold = 64 KB`, `resumeWriterThreshold = 32 KB`                                                |
| **AOT Compatibility** | `PublishAot=true`, `IsAotCompatible=true` — no reflection, no generics limitations                                         |
| **Memory**            | `ArrayPool<byte>` + CAS single-slot `HttpRequestPool` → **0 B** heap allocation per request                                |

### ❌ Not Supported

| Area                          | Detail                                                                                          |
|-------------------------------|-------------------------------------------------------------------------------------------------|
| **HTTP/2 & HTTP/3**           | HTTP/1.x only                                                                                   |
| **TLS / HTTPS**               | No SSL/TLS wrapper                                                                              |
| **Chunked Transfer Encoding** | `Transfer-Encoding: chunked` is not parsed; body is ignored                                     |
| **Request URL Decoding**      | `%xx` percent-encoding and `+`→space conversion are not performed                               |
| **Routing**                   | No built-in route matcher / middleware system                                                   |
| **WebSocket**                 | No upgrade flow                                                                                 |
| **Trailer Headers**           | No HTTP/1.1 trailer support                                                                     |
| **100-Continue**              | `Expect: 100-continue` is not answered                                                          |
| **IPv6**                      | IPv4 only (`AddressFamily.InterNetwork`)                                                        |
| **Graceful Shutdown**         | Active connections are not awaited when the `CancellationToken` is cancelled; accept loop stops |

---

## Quick Start

```csharp
using Anka;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var server = new Server(
    handler: async (req, res, ct) =>
    {
        var body = "Hello from Anka!"u8.ToArray();
        await res.WriteAsync(200, body, contentType: "text/plain; charset=utf-8", cancellationToken: ct);
    },
    port: 8080);

await server.StartAsync(cts.Token);
```

Graceful shutdown with `Ctrl+C`.

---

## Architecture Overview

```
                        ┌──────────────────────────────────────────┐
                        │               Server (public)            │
                        │  • Validates port & IP                   │
                        │  • Socket.Listen(backlog: 512)           │
                        │  • Accept loop → Connection.RunAsync()   │
                        └────────────────┬─────────────────────────┘
                                         │ fire & forget Task per client
                        ┌────────────────▼─────────────────────────┐
                        │            Connection (internal)         │
                        │                                          │
                        │   FillPipeAsync()   ReadPipeAsync()      │
                        │   ┌─────────────┐  ┌────────────────┐    │
                        │   │ Socket.Recv │  │ HttpParser     │    │
                        │   │ → PipeWriter│  │  .TryParse()   │    │
                        │   └──────┬──────┘  └───────┬────────┘    │
                        │          │    Pipe         │             │
                        │          └─────────────────┘             │
                        │                    │                     │
                        │          ┌─────────▼──────────┐          │
                        │          │  RequestHandler()  │          │
                        │          │  (user code)       │          │
                        │          └─────────┬──────────┘          │
                        │                    │                     │
                        │          ┌─────────▼──────────┐          │
                        │          │ HttpResponseWriter │          │
                        │          │  .WriteAsync()     │          │
                        │          └─────────┬──────────┘          │
                        │                    │                     │
                        │                    ▼ Socket.Send         │
                        └──────────────────────────────────────────┘
```

**Data Flow:**

```
TCP bytes → PipeWriter → [Pipe buffer] → PipeReader
         → HttpParser.ScanForComplete()  (Phase 1 — zero allocation)
         → HttpParser.ParseCore()        (Phase 2 — 1-2 ArrayPool.Rent)
         → HttpRequest                   (taken from pool)
         → handler(request, response)    (user code)
         → request.Return()              (buffers → ArrayPool, instance → pool)
```

---

## Application Lifecycle

### Step 1 — Creating the Server

```csharp
new Server(handler, port: 8080)
```

The `Server` constructor:
1. Port range check: `port < 1 || port > 65535` → throws `AnkaPortOutOfRageException`
2. IP parse check: `IPAddress.TryParse(host)` fails → throws `AnkaArgumentException`
3. `_endPoint = new IPEndPoint(ip, port)` — no socket yet

---

### Step 2 — Server.StartAsync()

```csharp
await server.StartAsync(cancellationToken);
```

1. `Socket(InterNetwork, Stream, Tcp)` is created
2. `socket.NoDelay = true` (Nagle algorithm disabled)
3. `socket.ReuseAddress = true`
4. `socket.Bind(_endPoint)` + `socket.Listen(backlog: 512)`
5. `Console.WriteLine($"Listening on {_endPoint}")` is printed
6. **Accept loop** starts:

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    var client = await socket.AcceptAsync(cancellationToken);
    _ = Connection.RunAsync(client, _handler, cancellationToken); // fire & forget
}
```

`Connection.RunAsync` is called fire-and-forget for each accepted connection.  
The acceptance loop **never** waits for a connection to finish.

---

### Step 3 — Connection.RunAsync()

```csharp
Connection.RunAsync(socket, handler, cancellationToken)
```

1. `socket.NoDelay = true` (also set per-connection)
2. `new Connection(socket, handler, cancellationToken)` is created
3. `ExecuteAsync()` is called:

```csharp
await Task.WhenAll(FillPipeAsync(), ReadPipeAsync());
// finally: socket.Close() + socket.Dispose()
```

Two async tasks run **concurrently**:

---

### Step 4 — FillPipeAsync() — Socket → Pipe

```
Socket.ReceiveAsync → PipeWriter.GetMemory(4096) → Advance(bytesRead) → FlushAsync
```

- A memory block of at least 4096 bytes is requested on each iteration
- `bytesRead == 0` → connection closed, loop ends
- `flush.IsCompleted` → reader has stopped consuming the pipe (slow client backpressure)
- `OperationCanceledException` / `SocketException` → exits silently
- `finally`: `PipeWriter.CompleteAsync()` → notifies the reader side

---

### Step 5 — ReadPipeAsync() — Pipe → Parse → Handle

```csharp
var responseWriter = new HttpResponseWriter(socket);

while (true)
{
    var result = await _pipe.Reader.ReadAsync(_cancellationToken);
    // ...
    if (HttpParser.TryParse(ref reader, out var request)) { ... }
    // ...
    _pipe.Reader.AdvanceTo(consumed, examined);
}
```

Each `ReadAsync` call returns the current Pipe buffer.  
`AdvanceTo(consumed, examined)`:
- `consumed`: this many bytes were truly consumed
- `examined`: this many bytes were inspected (but returned if insufficient)

In keep-alive mode the loop waits for the next request.  
`Connection: close` or HTTP/1.0 (without a keep-alive header) → `break` → connection closes.

---

### Step 6 — HttpParser.TryParse() — Two-Phase Parse

#### Phase 1: ScanForComplete (zero allocation)

```
advance over a copy of the reader (scanner):
  1. Skip the request line (\r\n)
  2. Scan header lines:
     - Line starts with "c" and is >15 bytes? → TryExtractContentLength()
     - Empty line (\r\n\r\n) → end of headers
  3. If body present: reader.Remaining >= contentLength?
```

This phase **makes no copies** — it only advances a pointer. Returns `false` if data is not enough.

#### Phase 2: ParseCore (1 or 2 ArrayPool.Rent)

```
buf = ArrayPool.Rent(headerWireBytes or min 256)
req = HttpRequestPool.Rent()  ← take from pool
req.Buffer = buf

  ParseRequestLine():
    METHOD SP path[?query] SP HTTP/x.y\r\n
    → HttpMethodParser.Parse(methodSpan)
    → HttpVersionParser.Parse(versionSpan)
    → path copied to buf; req.SetPath(offset, length)
    → query copied to buf; req.SetQuery(offset, length)

  Headers loop:
    Each \r\n-terminated line:
      "Name: Value" → headers.Add(name, value)
    Empty line → end

  Body (contentLength > 0):
    bodyBuf = ArrayPool.Rent(contentLength)
    reader.TryCopyTo(bodyBuf)
    req.BodyBuffer = bodyBuf
    req.Body = bodyBuf.AsMemory(0, contentLength)

  ComputeKeepAlive():
    Inspect Connection header → determine IsKeepAlive

  request = req
  success = true
```

**On failure** (finally block):
- `req != null` → `HttpRequestPool.Return(req)` → `Reset()` → both buffers returned to ArrayPool
- `req == null` (before Rent() throws): `buf` + `bodyBuf` returned manually

---

### Step 7 — Handler Invocation

```csharp
await _handler(request!, responseWriter, _cancellationToken);
```

User code at this point:
- Reads `request.Method`, `request.Path`, `request.QueryString`, `request.Headers`, `request.Body`
- Calls `response.WriteAsync(statusCode, body, contentType, keepAlive)`

---

### Step 8 — HttpResponseWriter.WriteAsync()

```
buf = ArrayPool.Rent(512 + smallBodyThreshold)

  WriteStatusLine()   → "HTTP/1.1 200 OK\r\n"
  WriteLiteral()      → "Server: Anka\r\n"
  WriteContentLength()→ "Content-Length: N\r\n"
  WriteLiteral()      → "Connection: keep-alive\r\n" or "Connection: close\r\n"
  (contentType)       → "Content-Type: ...\r\n"
  span[pos++] = '\r'; span[pos++] = '\n'  ← end of headers

  body <= 4096: body inlined → single SendAsync
  body >  4096: headers sent, then body in a separate SendAsync
```

`finally`: `ArrayPool.Return(buf)`

---

### Step 9 — request.Return()

```csharp
request!.Return(); // finally block inside Connection.ReadPipeAsync
```

`Return()` → `HttpRequestPool.Return(this)`:
1. `req.Reset()`:
   - `Buffer` → `ArrayPool.Return(Buffer)` → `Buffer = null`
   - `BodyBuffer` → `ArrayPool.Return(BodyBuffer)` → `BodyBuffer = null`
   - All fields zeroed (Method, Version, Path, Query, Headers, Body, IsKeepAlive)
2. `Interlocked.CompareExchange(ref _slot, req, null)`:
   - Slot empties: instance placed in pool (ready for the next request)
   - Slot full: instance left for GC

---

### Step 10 — Shutdown

When `CancellationToken.Cancel()` is called:

1. `socket.AcceptAsync` → throws `OperationCanceledException`
2. `Server.StartAsync` catches it → `Console.WriteLine("Server shutting down.")`
3. `using var socket` → dispose → server socket closes
4. Active `Connection` tasks: `ReceiveAsync` in `FillPipeAsync` and `ReadAsync` in `ReadPipeAsync` share the same token → `OperationCanceledException` → exits silently
5. Every `Connection.ExecuteAsync` finally block → `socket.Close()` + `socket.Dispose()`

---

## Class Reference — Public API

### `Server`

```
Namespace: Anka
Access:    public sealed
```

**Constructor**

```csharp
Server(RequestHandler handler, int port, string host = "127.0.0.1")
```

| Parameter | Description                                      |
|-----------|--------------------------------------------------|
| `handler` | Delegate called for every HTTP request           |
| `port`    | TCP port number (1–65535)                        |
| `host`    | IPv4 address to listen on (default: `127.0.0.1`) |

**Thrown Exceptions:**
- `AnkaPortOutOfRageException` — port is outside the 1–65535 range
- `AnkaArgumentException` — invalid IP address

**Method**

```csharp
Task StartAsync(CancellationToken cancellationToken = default)
```

Starts the server. Does not return until the token is cancelled. Returning = server stopped.

---

### `HttpRequest`

```
Namespace: Anka
Access:    public sealed
```

Represents a parsed HTTP request. **Must not be used** after the handler completes — the object is returned to the pool via `Return()`.

| Member        | Type                   | Description                                                   |
|---------------|------------------------|---------------------------------------------------------------|
| `Method`      | `HttpMethod`           | GET, POST, ...                                                |
| `Version`     | `HttpVersion`          | Http10, Http11                                                |
| `Path`        | `string`               | Lazy-materialized path string (`/api/users`)                  |
| `PathBytes`   | `ReadOnlySpan<byte>`   | Zero-copy raw path bytes                                      |
| `QueryString` | `string?`              | Lazy-materialized query (`foo=bar`). `null` if no `?` present |
| `QueryBytes`  | `ReadOnlySpan<byte>`   | Zero-copy raw query bytes                                     |
| `Headers`     | `HttpHeaders`          | Header collection (struct, inline)                            |
| `Body`        | `ReadOnlyMemory<byte>` | Request body. Empty if no Content-Length.                     |
| `IsKeepAlive` | `bool`                 | Is the connection persistent?                                 |

**Note:** `Path` and `QueryString` call `Encoding.ASCII.GetString()` on first access and cache the result. `PathBytes` and `QueryBytes` never allocate.

---

### `HttpResponseWriter`

```
Namespace: Anka
Access:    public sealed
```

Writes an HTTP/1.1 response. Zero string allocation via `ArrayPool` + `Utf8Formatter`.

**Method**

```csharp
ValueTask WriteAsync(
    int statusCode,
    ReadOnlyMemory<byte> body           = default,
    string? contentType                 = null,
    bool keepAlive                      = true,
    CancellationToken cancellationToken = default)
```

| Parameter      | Description                                            |
|----------------|--------------------------------------------------------|
| `statusCode`   | HTTP status code (200, 404, 500, etc.)                 |
| `body`         | Response body (optional)                               |
| `contentType`  | Content-Type header value (optional)                   |
| `keepAlive`    | `Connection: keep-alive` or `close`?                   |

**Supported Status Code Reason Phrases:**
200 OK · 201 Created · 204 No Content · 301 Moved Permanently · 302 Found · 304 Not Modified · 400 Bad Request · 401 Unauthorized · 403 Forbidden · 404 Not Found · 405 Method Not Allowed · 500 Internal Server Error · 503 Service Unavailable · others → "Unknown"

---

### `HttpHeaders`

```
Namespace: Anka
Access:    public struct
```

Header collection without heap allocation. 64 header entries are embedded in the struct via `[InlineArray(64)]`.

| Member                                                    | Description                                                       |
|-----------------------------------------------------------|-------------------------------------------------------------------|
| `Count`                                                   | Number of headers added                                           |
| `TryGetValue(ReadOnlySpan<byte>, out ReadOnlySpan<byte>)` | Zero-alloc lookup. `lowercaseName` **must already be lowercase**. |
| `TryGetValue(string, out ReadOnlySpan<byte>)`             | Lowercase conversion via `stackalloc`. 128-character limit.       |

**Important:** Header names are **lowercase-normalised** during `Add()`. Lookup is always done with `SequenceEqual`.  
`HttpHeaderNames` constants are already lowercase and can be used directly:

```csharp
if (request.Headers.TryGetValue(HttpHeaderNames.ContentType, out var ct))
{
    // ct = ReadOnlySpan<byte> — zero allocation
}
```

---

### `HttpHeaderNames`

```
Namespace: Anka
Access:    public static
```

Provides commonly used header names as lowercase `ReadOnlySpan<byte>`.

```
Host · Connection · ContentLength · ContentType · TransferEncoding
Accept · AcceptEncoding · Authorization · UserAgent · CacheControl · Cookie · Origin · Referer
```

---

### `HttpMethod` (enum)

```csharp
public enum HttpMethod : byte
{ Unknown=0, Get, Post, Put, Delete, Head, Options, Patch, Trace, Connect }
```

---

### `HttpVersion` (enum)

```csharp
public enum HttpVersion : byte
{ Unknown=0, Http10=1, Http11=2 }
```

---

### `RequestHandler` (delegate)

```csharp
public delegate ValueTask RequestHandler(
    HttpRequest request,
    HttpResponseWriter response,
    CancellationToken cancellationToken);
```

The user callback passed to the server. Called for every HTTP request.

---

### `AnkaArgumentException`

Derives from `ArgumentException`. Thrown when an invalid argument is provided (e.g. invalid IP).

### `AnkaPortOutOfRageException`

Derives from `ArgumentOutOfRangeException`. Thrown when the port is outside the 1–65535 range.

---

## Class Reference — Internal

### `Connection` (internal sealed)

Manages the lifecycle of each TCP connection.

| Member                                 | Description                                                                                               |
|----------------------------------------|-----------------------------------------------------------------------------------------------------------|
| `static RunAsync(socket, handler, ct)` | Single public entry point. Sets `socket.NoDelay=true`, creates a new `Connection`, runs `ExecuteAsync()`. |
| `ExecuteAsync()`                       | Runs `Task.WhenAll(FillPipeAsync, ReadPipeAsync)`. `finally`: `socket.Close()` + `Dispose()`              |
| `FillPipeAsync()`                      | Socket → PipeWriter. Continuous `ReceiveAsync` → `Advance` → `FlushAsync`.                                |
| `ReadPipeAsync()`                      | PipeReader → `HttpParser.TryParse` → `handler` → `request.Return()`. Keep-alive loop lives here.          |

**PipeOptions:**
```
pauseWriterThreshold:  64 KB  (slow reader → stop writing)
resumeWriterThreshold: 32 KB  (when buffer drops to half → resume)
useSynchronizationContext: false  (run on ThreadPool)
```

---

### `HttpParser` (internal static)

Parses HTTP/1.x requests.

| Member                                                 | Description                                                  |
|--------------------------------------------------------|--------------------------------------------------------------|
| `TryParse(ref SequenceReader<byte>, out HttpRequest?)` | Two-phase parse. Returns `false` if data is insufficient.    |
| `ScanForComplete(ref reader, out contentLength)`       | Phase 1: zero-allocation scan                                |
| `TryExtractContentLength(line, ref contentLength)`     | Extracts the `content-length:` header value                  |
| `ParseRequestLine(seq, buf, ref writePos, req)`        | Parses method + path + query + version                       |
| `ParseHeaderLine(seq, ref headers)`                    | Parses a single header line                                  |
| `AddHeaderFromSpan(line, ref headers)`                 | Splits `Name: Value`, calls `headers.Add()`                  |
| `ComputeKeepAlive(version, ref headers)`               | Computes `IsKeepAlive` from HTTP version + Connection header |

---

### `HttpMethodParser` (internal static)

| Member                      | Description                                                 |
|-----------------------------|-------------------------------------------------------------|
| `Parse(ReadOnlySpan<byte>)` | Byte span → `HttpMethod` enum. Unknown → `Unknown`          |
| `ToBytes(this HttpMethod)`  | `HttpMethod` enum → `ReadOnlySpan<byte>` (extension method) |

---

### `HttpVersionParser` (internal static)

| Member                      | Description                                                         |
|-----------------------------|---------------------------------------------------------------------|
| `Parse(ReadOnlySpan<byte>)` | `"HTTP/1.1"` → `Http11`, `"HTTP/1.0"` → `Http10`, other → `Unknown` |

---

### `HttpRequestPool` (internal static)

CAS-based single-slot object pool. Lock-free, AOT-safe.

| Member        | Description                                                                                                       |
|---------------|-------------------------------------------------------------------------------------------------------------------|
| `Rent()`      | `Interlocked.Exchange(ref _slot, null)` — returns slot if full, otherwise `new HttpRequest()`                     |
| `Return(req)` | `req.Reset()` then `Interlocked.CompareExchange(ref _slot, req, null)` — if slot is full, instance is left for GC |

**Why not ConcurrentQueue?**  
`ConcurrentQueue<T>` allocates a new internal segment (~608 bytes) every 32 enqueue/dequeue operations. This caused 608 B to appear in microbenchmarks. A CAS single-slot achieves **0 B**.

---

## Memory Model

```
Memory movement per HTTP request:

  HttpParser.TryParse()
  ├── ArrayPool.Rent(headerWireBytes)     ← buf: path + query + header names/values
  ├── HttpRequestPool.Rent()              ← HttpRequest instance (from pool or new)
  └── ArrayPool.Rent(contentLength)       ← bodyBuf (only if body present)

  handler() runs...

  request.Return()
  ├── ArrayPool.Return(buf)
  ├── ArrayPool.Return(bodyBuf)           ← only if body present
  └── HttpRequestPool._slot = req         ← place in single slot via CAS

  HttpResponseWriter.WriteAsync()
  ├── ArrayPool.Rent(512 + bodyThreshold) ← header buffer
  └── ArrayPool.Return(buf)               ← finally
```

**Result:** **0 heap allocations** for repeated requests (thanks to `ArrayPool` and the object pool).

---

## Performance Profile

BenchmarkDotNet (Release / net10.0) measurements:

| Scenario                         | Mean    | Gen0 | Allocated |
|----------------------------------|---------|------|-----------|
| SimpleGet (minimal GET)          | ~129 ns | 0    | **0 B**   |
| GetWithManyHeaders (10 headers)  | ~380 ns | 0    | **0 B**   |
| PostWithSmallBody (128 B body)   | ~160 ns | 0    | **0 B**   |
| PostWithLargeBody (64 KB body)   | ~4.1 µs | 0    | **0 B**   |
| Header lookup — hit (byte span)  | ~8 ns   | 0    | 0 B       |
| Header lookup — miss (byte span) | ~5 ns   | 0    | 0 B       |
| HttpMethod.Parse (GET)           | ~0.3 ns | 0    | 0 B       |

**Comparison with other servers (parsing layer):**

| Server / Layer         | Alloc / request | Notes                                        |
|------------------------|-----------------|----------------------------------------------|
| Anka                   | **0 B**         | ArrayPool + CAS pool                         |
| ASP.NET Core (Kestrel) | ~300–600 B      | Framework overhead (routing, middleware, DI) |
| HttpListener           | ~1–2 KB         | Managed objects per request                  |

*Note: This comparison measures the parsing layer only. Kestrel's routing and middleware pipeline offer different trade-offs.*

---

## Project Structure

```
Anka/
├── Anka.slnx
│
├── Anka/                          ← Library
│   ├── Anka.csproj                  (AOT, InternalsVisibleTo: Test + Benchmark)
│   └── src/
│       ├── Core/                  ← Public API
│       │   ├── HttpHeaderNames.cs   (pre-defined header name constants)
│       │   ├── HttpHeaders.cs       (zero-alloc header struct, InlineArray)
│       │   ├── HttpMethod.cs        (enum: byte)
│       │   ├── HttpRequest.cs       (parsed request + buffer ownership)
│       │   ├── HttpResponseWriter.cs(response writer, ArrayPool)
│       │   ├── HttpVersion.cs       (enum: byte)
│       │   ├── RequestHandler.cs    (delegate definition)
│       │   └── Server.cs            (public entry point)
│       ├── Internal/              ← Implementation details (internal)
│       │   ├── Connection.cs        (socket lifecycle + pipe management)
│       │   ├── HttpMethodParser.cs  (byte span → HttpMethod enum)
│       │   ├── HttpParser.cs        (two-phase HTTP/1.x parser)
│       │   ├── HttpRequestPool.cs   (CAS single-slot object pool)
│       │   └── HttpVersionParser.cs (byte span → HttpVersion enum)
│       └── Exceptions/
│           ├── AnkaArgumentException.cs
│           └── AnkaPortOutOfRageException.cs
│
├── Anka.Console/                  ← Sample application
│   └── Program.cs                   (Hello World server on :8080)
│
├── Anka.Test/                     ← xUnit tests (108 tests)
│   ├── HttpHeadersTests.cs
│   ├── HttpMethodParserTests.cs
│   ├── HttpParserTests.cs
│   ├── HttpRequestTests.cs
│   ├── HttpVersionParserTests.cs
│   └── ServerTests.cs
│
└── Anka.Benchmark/                ← BenchmarkDotNet micro-benchmarks
    ├── HttpHeadersBenchmarks.cs
    ├── HttpMethodParserBenchmarks.cs
    ├── HttpParserBenchmarks.cs
    └── HttpVersionParserBenchmarks.cs
```

---

## Contributing and Development

### Running Tests

```bash
dotnet test Anka.Test
```

### Running Benchmarks

```bash
cd Anka.Benchmark
dotnet run -c Release
```

### Load Testing

Start the server:
```bash
cd Anka.Console
dotnet run -c Release
```

Load test with external tools:
```bash
# wrk
wrk -t4 -c100 -d30s http://localhost:8080/

# hey
hey -n 100000 -c 100 http://localhost:8080/
```
