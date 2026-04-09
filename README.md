# Anka

[![NuGet](https://img.shields.io/nuget/v/Anka.svg)](https://www.nuget.org/packages/Anka)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Anka.svg)](https://www.nuget.org/packages/Anka)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8+](https://img.shields.io/badge/.NET-8.0%2B-512BD4)](https://dotnet.microsoft.com)

Minimal HTTP/1.x server library for .NET 8+, built for **Native AOT** with a focus on minimising cold-start time and keeping steady-state allocation at zero.

---

## Table of Contents

1. [Installation](#installation)
2. [Why Anka](#why-anka)
3. [Quick Start](#quick-start)
4. [Examples](#examples)
5. [Architecture Overview](#architecture-overview)
6. [Class Reference — Public API](#class-reference--public-api)
7. [Class Reference — Internal](#class-reference--internal)
8. [Memory Model](#memory-model)
9. [Performance Profile](#performance-profile)
10. [Project Structure](#project-structure)

---

## Installation

```shell
dotnet add package Anka
```

[![NuGet](https://img.shields.io/nuget/v/Anka.svg)](https://www.nuget.org/packages/Anka)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Anka.svg)](https://www.nuget.org/packages/Anka)

Requires .NET 8 SDK or later.

---

## Why Anka

Modern .NET applications typically pay 100–300 ms of JIT warmup on every cold start. In serverless and container environments, every millisecond costs money and latency. Anka is designed around one idea:

> **Publish as a Native AOT binary. Be ready to serve the first request in under 25 ms.**

| | Anka (Native AOT) | Kestrel (JIT) |
|---|---:|---:|
| Time to listen | 411 ms ¹ | 203 ms |
| **Time to ready** | **2.3 ms** | **140 ms** |
| First response | 20 ms | 26 ms |
| Startup allocation | **124.5 KB** | 2.5 MB |
| RSS at steady state | **~15 MB** | ~98 MB |

¹ Anka creates a fresh `Socket`; Kestrel reuses existing OS handles.

To achieve this, Anka makes deliberate trade-offs:

- **No middleware pipeline** — a single `RequestHandler` delegate handles every request
- **No built-in routing** — path dispatch is left to user code (a `switch` expression is enough)
- **HTTP/1.x only** — no HTTP/2, no TLS, no WebSocket
- **Raw sockets** — `SocketAsyncEventArgs` + pooled 64 KB sliding receive window, no `System.IO.Pipelines`

If you need the full ASP.NET Core feature set, use Kestrel. If you need a tiny, fast, zero-allocation HTTP listener for a Native AOT binary — Anka is for you.

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

## Examples

### Plain Text Response

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        var body = "Hello, World!"u8.ToArray();
        await res.WriteAsync(200, body, contentType: "text/plain; charset=utf-8", cancellationToken: ct);
    },
    port: 8080);

await server.StartAsync(cts.Token);
```

### JSON Response

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        var json = """{"message":"ok","version":"0.0.1"}"""u8.ToArray();
        await res.WriteAsync(200, json, contentType: "application/json; charset=utf-8", cancellationToken: ct);
    },
    port: 8080);
```

### Reading Path and Query String

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        // req.Path   → "/search"
        // req.Query  → "q=anka&limit=10"
        var path = req.Path.ToString();
        var query = req.Query.ToString();

        var body = System.Text.Encoding.UTF8.GetBytes($"path={path} query={query}");
        await res.WriteAsync(200, body, contentType: "text/plain; charset=utf-8", cancellationToken: ct);
    },
    port: 8080);
```

### Reading Request Headers

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        // Header names are normalised to lowercase
        var ua = req.Headers.TryGetValue("user-agent", out var v) ? v.ToString() : "unknown";

        var body = System.Text.Encoding.UTF8.GetBytes($"User-Agent: {ua}");
        await res.WriteAsync(200, body, contentType: "text/plain; charset=utf-8", cancellationToken: ct);
    },
    port: 8080);
```

### Reading POST Body

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        // req.Body contains the full body (read via Content-Length)
        var text = System.Text.Encoding.UTF8.GetString(req.Body.Span);

        var echo = System.Text.Encoding.UTF8.GetBytes($"echo: {text}");
        await res.WriteAsync(200, echo, contentType: "text/plain; charset=utf-8", cancellationToken: ct);
    },
    port: 8080);
```

### Simple Path-Based Routing

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        var path = req.Path.ToString();
        var method = req.Method.ToString();

        (int status, byte[] body, string ct2) = (path, method) switch
        {
            ("/", "GET")     => (200, "Welcome!"u8.ToArray(),           "text/plain; charset=utf-8"),
            ("/ping", "GET") => (200, """{"pong":true}"""u8.ToArray(),  "application/json; charset=utf-8"),
            _                => (404, "Not Found"u8.ToArray(),          "text/plain; charset=utf-8")
        };

        await res.WriteAsync(status, body, contentType: ct2, cancellationToken: ct);
    },
    port: 8080);
```

---

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
                        │  SocketReceiver.ReceiveAsync()           │
                        │  → pooled 64 KB receive buffer           │
                        │  → sliding parse window                  │
                        │  → HttpParser.TryParse()                 │
                        │  → RequestHandler()                      │
                        │  → HttpResponseWriter.WriteAsync()       │
                        │  → Socket.SendAsync()                    │
                        └──────────────────────────────────────────┘
```

**Data Flow:**

```
TCP bytes → SocketReceiver.ReceiveAsync()
         → pooled receive buffer + sliding window
         → HttpParser.TryParse()
         → HttpRequest (rented once per connection, reused per request)
         → handler(request, response)
         → HttpResponseWriter.WriteAsync()
         → Socket.SendAsync()
```

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

| Member                                 | Description                                                                                                  |
|----------------------------------------|--------------------------------------------------------------------------------------------------------------|
| `static RunAsync(socket, handler, ct)` | Single public entry point. Sets `socket.NoDelay=true`, creates a new `Connection`, runs `ProcessAsync()`.   |
| `ProcessAsync()`                       | Owns the receive loop, parser loop, handler dispatch, keep-alive lifecycle, and connection-scoped resources  |
| Sliding receive window                 | Avoids compacting unread bytes after every request; compacts only when the receive tail is full              |
| `finally` cleanup                      | Returns the `HttpRequest` to `HttpRequestPool`, returns pooled buffers, closes and disposes the socket       |

---

### `SocketReceiver` (internal sealed)

Zero-allocation socket receive wrapper. One instance per connection; must be `Dispose()`d on close.

| Member                                 | Description                                                                                                                                                                      |
|----------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `ReceiveAsync(socket, buffer)`         | Returns a `ValueTask<int>`. Sync path: data already in kernel buffer → no allocation, no thread switch. Async path: OS I/O thread posts continuation to `ThreadPool` via `IValueTaskSource`. |
| `Dispose()`                            | Disposes the underlying `SocketAsyncEventArgs`                                                                                                                                  |

`RunContinuationsAsynchronously = true` on the internal `ManualResetValueTaskSourceCore<int>` ensures the OS kqueue/epoll I/O thread is never blocked by request-processing work.

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

The parser uses a short length/byte dispatch instead of chaining multiple `SequenceEqual` calls. For fixed ASCII method tokens this reduces repeated comparisons on the hot path and produces a simpler branch tree for the JIT.

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
Memory movement per connection:

  Connection.ProcessAsync()
  ├── ArrayPool.Rent(64 KB)               ← receive buffer (reused across all requests on the connection)
  ├── HttpRequestPool.Rent()              ← HttpRequest instance (reused across requests on the connection)
  ├── HttpResponseWriter(socket)          ← connection-scoped response buffer
  └── SocketReceiver()                    ← connection-scoped SocketAsyncEventArgs

Memory movement per request:

  HttpParser.TryParse()
  ├── Reuses request.Buffer when large enough
  ├── Reuses request.BodyBuffer when large enough
  └── Copies only path/query/header slices and any Content-Length body

Connection closes:
  ├── HttpRequestPool.Return(request)
  ├── ArrayPool.Return(receive buffer)
  ├── SocketReceiver.Dispose()            ← disposes SocketAsyncEventArgs
  └── HttpResponseWriter.Dispose()        ← returns response buffer
```

**Result:** repeated requests on an existing connection stay on an allocation-free fast path in the parser microbenchmarks, while connection startup still pays its one-time pooled buffer rentals.

---

## Performance Profile

> **Environment:** Apple M3 Max · 16 logical cores · macOS 26.3.1 · .NET 8.0.25 · Native AOT (osx-arm64)  
> Microbenchmarks: `dotnet run --project Benchmark/Anka.Benchmark -c Release`  
> End-to-end: `Test/LoadTest/Anka.Wrk.LoadTest` (wrk, 10 s per level, loopback)  
> Full results: [`docs/`](docs/) — one file per OS per run (e.g. `throughput-results-macos-2026-04-08.md`)

### Running Benchmarks on Linux

Docker or Podman is required. PostgreSQL is started automatically — no manual setup needed.

```shell
# linux/amd64 (default)
./scripts/run-linux-benchmark.sh

# linux/arm64 — runs natively on Apple Silicon (much faster, no emulation)
./scripts/run-linux-benchmark.sh linux/arm64
```

The script spins up PostgreSQL, initialises the schema, runs the full suite (framework + DB tests), then tears everything down. Results are written to `docs/throughput-results-linux-{date}.md`.

---

### Startup Snapshot

| | Anka (Native AOT) | Kestrel (JIT) |
|---|---:|---:|
| Time to listen | 411 ms | 203 ms |
| Time to ready | **2.3 ms** | 140 ms |
| First response | 20 ms | 26 ms |
| Startup alloc | **124.5 KB** | 2.50 MB |
| RSS after first response | **15.1 MB** | 97.9 MB |

> Kestrel binds the port faster because it reuses existing OS handles; Anka is slower because it creates a fresh `Socket`. JIT warmup adds ~140 ms to Kestrel's "ready" time.

---

### Microbenchmarks — zero allocations throughout

> Run with `dotnet run --project Benchmark/Anka.Benchmark -c Release`  
> BenchmarkDotNet v0.15.8 · .NET 8.0.25 · Arm64 RyuJIT

#### HTTP Parser

> Parses a complete raw HTTP/1.x byte buffer into `HttpRequest` with no heap allocation.

| Benchmark | Mean | Allocated |
|---|---:|---:|
| SimpleGet | 92.9 ns | **0 B** |
| GetWithManyHeaders (10 headers) | 420.0 ns | **0 B** |
| PostWithSmallBody (256 B body) | 244.6 ns | **0 B** |
| PostWithLargeBody (64 KB body) | 1,651 ns | **0 B** |

#### HTTP Headers

> `HttpHeaders` is an inline-array struct — no heap allocation on add or lookup.

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Add_TenHeaders | 75.2 ns | **0 B** |
| TryGetValue — byte span, first entry | 84.8 ns | **0 B** |
| TryGetValue — byte span, last entry | 94.3 ns | **0 B** |
| TryGetValue — byte span, missing | 92.7 ns | **0 B** |
| TryGetValue — string, case-insensitive | 94.6 ns | **0 B** |

#### HTTP Method Parser

> Byte-span trie — common verbs resolve in sub-nanosecond time.

| Method | Mean |
|---|---:|
| GET | 0.71 ns |
| POST | 0.99 ns |
| PUT | 0.71 ns |
| HEAD | 0.98 ns |
| PATCH | 1.32 ns |
| DELETE | 1.57 ns |
| OPTIONS | 1.83 ns |
| CONNECT | 1.90 ns |

#### HTTP Version Parser

| Version | Mean |
|---|---:|
| HTTP/1.1 | 0.09 ns |
| HTTP/1.0 | 0.21 ns |

---

### End-to-End Throughput — Framework Tests (wrk · c = 400)

> No database. Raw HTTP pipeline throughput on loopback.

| Scenario | Anka (AOT) req/s | Kestrel (JIT) req/s |
|---|---:|---:|
| Plain Text GET | 133,000 | 141,700 |
| JSON API GET | 131,600 | 140,400 |
| GET w/ Multiple Headers | 133,300 | 140,900 |
| POST Echo (256 B body) | 127,100 | 131,200 |
| Large Response GET (~2 KB) | 129,800 | 135,700 |

> Anka and Kestrel deliver comparable throughput on loopback. Kestrel's JIT-generated native code edges ahead at high concurrency due to optimisations that are not available to the AOT compiler. Anka's advantage is memory: **~15 MB RSS** vs **~98 MB** at steady state, and near-zero startup allocation (124.5 KB vs 2.5 MB).

---

### End-to-End Throughput — TechEmpower-Style DB Tests (PostgreSQL · peak req/s)

| Scenario | Anka (AOT) | Kestrel (JIT) |
|---|---:|---:|
| Single DB Query | 23,500 | 24,500 |
| Multiple Queries (20) | 1,300 | 1,300 |
| Fortunes | 22,300 | 23,400 |
| DB Updates (20) | 622 | 629 |
| Cached Queries (100) | 107,200 | 145,600 |

> DB-bound tests are limited by PostgreSQL connection pool saturation, not by the HTTP layer. Per-concurrency-level detail tables are in [`docs/`](docs/).

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
│       │   ├── Connection.cs        (socket lifecycle + sliding receive window)
│       │   ├── HttpMethodParser.cs  (byte span → HttpMethod enum)
│       │   ├── HttpParser.cs        (two-phase HTTP/1.x parser)
│       │   ├── HttpRequestPool.cs   (CAS single-slot object pool)
│       │   ├── HttpVersionParser.cs (byte span → HttpVersion enum)
│       │   └── SocketReceiver.cs    (zero-alloc SocketAsyncEventArgs + IValueTaskSource)
│       └── Exceptions/
│           ├── AnkaArgumentException.cs
│           └── AnkaPortOutOfRageException.cs
│
├── Anka.Console/                  ← Sample application
│   └── Program.cs                   (Hello World server on :8080)
│
├── Anka.Test/                     ← xUnit tests
│   ├── HttpHeadersTests.cs
│   ├── HttpMethodParserTests.cs
│   ├── HttpParserTests.cs
│   ├── HttpRequestTests.cs
│   ├── HttpVersionParserTests.cs
│   └── ServerTests.cs
│
├── Test/LoadTest/
│   ├── Anka.HttpConsole/          ← Native AOT load-test target
│   ├── Kestrel.HttpConsole/       ← Minimal ASP.NET Core comparison target
│   └── Anka.Wrk.LoadTest/         ← wrk-based startup + throughput harness
├── Benchmark/Anka.Benchmark/      ← BenchmarkDotNet micro-benchmarks
│   ├── HttpHeadersBenchmarks.cs
│   ├── HttpMethodParserBenchmarks.cs
│   ├── HttpParserBenchmarks.cs
│   └── HttpVersionParserBenchmarks.cs
├── scripts/
│   └── run-linux-benchmark.sh     ← Docker helper: run load test on Linux
└── Dockerfile.benchmark           ← Linux load-test image (wrk + .NET SDK + AOT tools)
```

---

## Contributing and Development

### Running Tests

```bash
dotnet test Anka.slnx --nologo
```

### Running Benchmarks

```bash
dotnet run --project Benchmark/Anka.Benchmark -c Release
```

### Load Testing

Run the comparison harness:
```bash
dotnet run --project Test/LoadTest/Anka.Wrk.LoadTest --configuration Release
```
