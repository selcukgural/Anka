# Anka

[![NuGet](https://img.shields.io/nuget/v/Anka.svg)](https://www.nuget.org/packages/Anka)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Anka.svg)](https://www.nuget.org/packages/Anka)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8+](https://img.shields.io/badge/.NET-8.0%2B-512BD4)](https://dotnet.microsoft.com)
[![Cold Start: 2.3ms](https://img.shields.io/badge/cold--start%20ready-2.3%20ms-brightgreen)](#cold-start--measured-results)
[![Startup Alloc: 124.5 KB](https://img.shields.io/badge/startup%20alloc-124.5%20KB-brightgreen)](#cold-start--measured-results)
[![Status: Beta](https://img.shields.io/badge/status-beta-orange)](#)

> ⚠️ **BETA — Research & Experimentation Only**
>
> Anka is in **early beta**. It is intended **solely for testing, research, and experimentation**.
> **Do not use Anka in production environments.** The API is unstable, security hardening is incomplete,
> and there are no guarantees around correctness, stability, or backwards compatibility.
> Production use is strongly discouraged.

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

> **Note:** Anka is currently in beta. Use only for research and experimentation — not for production workloads.

---

## Why Anka

Modern .NET applications typically pay 100–300 ms of JIT warmup on every cold start. In serverless and container environments — where instances are spun up on demand — every millisecond of startup latency translates directly into cost and tail latency for the first caller.

Anka is designed around one idea:

> **Publish as a Native AOT binary. Accept the first HTTP request in under 25 ms from process launch.**

### Cold Start — Measured Results

> **Environment:** Apple M3 Max · macOS · .NET 8.0.25 · Native AOT (osx-arm64)  
> "Time to ready" = time between process start and the socket becoming ready to accept connections.  
> "First response" = round-trip time of the very first HTTP request, measured from outside the process.

| | Anka (Native AOT) | Kestrel (JIT) | Improvement |
|---|---:|---:|---:|
| Time to listen | 411 ms ¹ | 203 ms | — |
| **⚡ Time to ready** | **2.3 ms** | **140 ms** | **61× faster** |
| First response | 20 ms | 26 ms | 1.3× faster |
| Startup allocation | **124.5 KB** | 2.5 MB | **20× less** |
| RSS at steady state | **~15 MB** | ~98 MB | **6.5× less** |

¹ Anka creates a fresh `Socket`; Kestrel reuses existing OS handles, so it binds the port faster. The JIT warmup cost more than compensates: Kestrel needs **140 ms** after binding before it can serve — Anka needs **2.3 ms**.

**What "2.3 ms ready" means in practice:**  
From the moment the OS hands control to the process, Anka allocates a socket, binds, and starts accepting connections in **2.3 milliseconds**. A Kestrel process in the same environment takes ~140 ms to reach the same point due to JIT compilation. In a serverless or autoscaling context this difference is the gap between a cold start that a user notices and one that goes undetected.

To achieve this, Anka makes deliberate trade-offs:

- **No middleware pipeline** — a single `RequestHandler` delegate handles every request
- **No built-in routing** — path dispatch is left to user code (a `switch` expression is enough)
- **HTTP/1.x only** — no HTTP/2, no TLS, no WebSocket
- **Raw sockets** — `SocketAsyncEventArgs` + pooled 64 KB sliding receive window, no `System.IO.Pipelines`

If you need the full ASP.NET Core feature set, use Kestrel. If you need a tiny, fast, zero-allocation HTTP listener with near-instant cold starts for a Native AOT binary — Anka is for you.

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
        await res.WriteAsync(200, body, contentType: "text/plain; charset=utf-8"u8, cancellationToken: ct);
    },
    port: 8080);

await server.StartAsync(cts.Token);
```

Graceful shutdown with `Ctrl+C`.

### Quick Start with Options

```csharp
var options = new ServerOptions
{
    MaxRequestBodySize = 1 * 1024 * 1024, // 1 MB
    MaxRequestTargetSize = 8 * 1024,      // 8 KB
    MaxRequestHeadersSize = 8 * 1024,     // 8 KB
    ReadTimeout = TimeSpan.FromSeconds(15),
    DefaultResponseHeaders =
    [
        new HttpHeader("x-content-type-options"u8.ToArray(), "nosniff"u8.ToArray()),
        new HttpHeader("x-frame-options"u8.ToArray(), "DENY"u8.ToArray()),
    ]
};

var server = new Server(
    handler: async (req, res, ct) =>
    {
        await res.WriteAsync(200, "ok"u8.ToArray(), "text/plain; charset=utf-8"u8, cancellationToken: ct);
    },
    port: 8080,
    options: options);

await server.StartAsync(cts.Token);
```

---

## Examples

### Plain Text Response

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        var body = "Hello, World!"u8.ToArray();
        await res.WriteAsync(200, body, contentType: "text/plain; charset=utf-8"u8, cancellationToken: ct);
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
        await res.WriteAsync(200, json, contentType: "application/json; charset=utf-8"u8, cancellationToken: ct);
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
        await res.WriteAsync(200, body, contentType: "text/plain; charset=utf-8"u8, cancellationToken: ct);
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
        await res.WriteAsync(200, body, contentType: "text/plain; charset=utf-8"u8, cancellationToken: ct);
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
        await res.WriteAsync(200, echo, contentType: "text/plain; charset=utf-8"u8, cancellationToken: ct);
    },
    port: 8080);
```

Conflicting duplicate `Content-Length` headers are rejected with `400 Bad Request`. Malformed `Content-Length` framing is also rejected with `400 Bad Request`.

Responses to `HEAD` requests suppress payload bytes on the wire while preserving representation headers. `1xx`, `204`, and `304` responses omit payload bytes and body-describing headers such as `Content-Length` and `Content-Type`.

`HTTP/1.1` requests must include a valid `Host` header. Missing, duplicate, invalid, or absolute-form-mismatched `Host` values are rejected with `400 Bad Request`.

Malformed HTTP version tokens are rejected with `400 Bad Request`. Well-formed but unsupported versions such as `HTTP/2.0` are rejected with `505 HTTP Version Not Supported`.

Repeated headers can be enumerated via `HttpHeaders.TryGetAllValues(...)`. `Expect: 100-continue` is handled automatically, and chunked request bodies are decoded into `req.Body` before the handler runs.

### Simple Path-Based Routing

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        var path = req.Path.ToString();
        var method = req.Method.ToString();

        (int status, byte[] body, ReadOnlyMemory<byte> ct2) = (path, method) switch
        {
            ("/", "GET")     => (200, "Welcome!"u8.ToArray(),           (ReadOnlyMemory<byte>)"text/plain; charset=utf-8"u8.ToArray()),
            ("/ping", "GET") => (200, """{"pong":true}"""u8.ToArray(),  (ReadOnlyMemory<byte>)"application/json; charset=utf-8"u8.ToArray()),
            _                => (404, "Not Found"u8.ToArray(),          (ReadOnlyMemory<byte>)"text/plain; charset=utf-8"u8.ToArray())
        };

        await res.WriteAsync(status, body, contentType: ct2, cancellationToken: ct);
    },
    port: 8080);
```

### Adding Per-Request Response Headers (Fluent API)

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        var body = """{"status":"ok"}"""u8.ToArray();

        // Fluent AddHeader chains extra headers onto the response.
        // For zero-allocation hot paths, pre-build a static readonly HttpHeader[] instead.
        await res
            .AddHeader(HttpHeaderNames.AccessControlAllowOrigin, "*"u8)
            .AddHeader(HttpHeaderNames.AccessControlAllowMethods, "GET, POST"u8)
            .WriteAsync(200, body, "application/json; charset=utf-8"u8, keepAlive: true, ct);
    },
    port: 8080);
```

### Redirect

```csharp
var server = new Server(
    handler: async (req, res, ct) =>
    {
        await res
            .AddHeader(HttpHeaderNames.Location, "/new-path"u8)
            .WriteAsync(301, default, default, keepAlive: false, ct);
    },
    port: 8080);
```

### Default Response Headers (Global Security Headers)

```csharp
var options = new ServerOptions
{
    DefaultResponseHeaders =
    [
        new HttpHeader("x-content-type-options"u8.ToArray(), "nosniff"u8.ToArray()),
        new HttpHeader("x-frame-options"u8.ToArray(),        "DENY"u8.ToArray()),
        new HttpHeader("x-xss-protection"u8.ToArray(),       "1; mode=block"u8.ToArray()),
    ]
};

var server = new Server(handler, port: 8080, options: options);
```

### Enforcing a Request Body Size Limit

```csharp
// Requests with a body exceeding 512 KB automatically receive 413 Payload Too Large.
var options = new ServerOptions
{
    MaxRequestBodySize = 512 * 1024,
    MaxRequestTargetSize = 8 * 1024,
    MaxRequestHeadersSize = 8 * 1024
};

var server = new Server(handler, port: 8080, options: options);
```

### Enforcing a Request Header Size Limit

```csharp
// Requests whose header fields exceed 8 KB, or exceed the built-in
// header-count cap, automatically receive 431 Request Header Fields Too Large.
var options = new ServerOptions
{
    MaxRequestHeadersSize = 8 * 1024
};

var server = new Server(handler, port: 8080, options: options);
```

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
Server(RequestHandler handler, int port, string host = "127.0.0.1", ServerOptions? options = null)
```

| Parameter | Description                                      |
|-----------|--------------------------------------------------|
| `handler` | Delegate called for every HTTP request           |
| `port`    | TCP port number (1–65535)                        |
| `host`    | IPv4 address to listen on (default: `127.0.0.1`) |
| `options` | Optional server configuration (see `ServerOptions`). Uses sensible defaults when `null`. |

**Event**

```csharp
event Action<IPEndPoint>? ListeningStarted
```

Raised once the listening socket has been bound. Useful for startup instrumentation and readiness probes.

**Thrown Exceptions:**
- `AnkaOutOfRangeException` — port is outside the 1–65535 range
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
| `Body`        | `ReadOnlyMemory<byte>` | Request body. Populated from `Content-Length` or decoded chunked bodies; empty when no body is present. |
| `IsKeepAlive` | `bool`                 | Is the connection persistent?                                 |

**Note:** `Path` and `QueryString` call `Encoding.ASCII.GetString()` on first access and cache the result. `PathBytes` and `QueryBytes` never allocate.

---

### `HttpResponseWriter`

```
Namespace: Anka
Access:    public sealed
```

Writes an HTTP/1.1 response. Zero string allocation via `ArrayPool` + `Utf8Formatter`.

**Methods**

```csharp
// Simple overload — no extra headers
ValueTask WriteAsync(
    int statusCode,
    ReadOnlyMemory<byte> body           = default,
    ReadOnlyMemory<byte> contentType    = default,
    bool keepAlive                      = true,
    CancellationToken cancellationToken = default)

// Full overload — with per-request extra headers
ValueTask WriteAsync(
    int statusCode,
    ReadOnlyMemory<byte> body,
    ReadOnlyMemory<byte> contentType,
    bool keepAlive,
    ReadOnlySpan<HttpHeader> extraHeaders,
    CancellationToken cancellationToken = default)
```

| Parameter      | Description                                                                        |
|----------------|------------------------------------------------------------------------------------|
| `statusCode`   | HTTP status code (200, 404, 500, etc.)                                             |
| `body`         | Response body (optional)                                                           |
| `contentType`  | Content-Type header value as UTF-8 bytes (e.g., `"application/json"u8`)            |
| `keepAlive`    | `Connection: keep-alive` or `close`?                                               |
| `extraHeaders` | Zero-allocation per-request headers. Pre-build a `static readonly HttpHeader[]` for hot paths. |

> **Fluent API:** Use `response.AddHeader(name, value).WriteAsync(...)` to attach extra headers without building an array. See `HttpResponseWriterExtensions`.

**Supported Status Code Reason Phrases:**
200 OK · 201 Created · 204 No Content · 301 Moved Permanently · 302 Found · 304 Not Modified · 400 Bad Request · 401 Unauthorized · 403 Forbidden · 404 Not Found · 405 Method Not Allowed · 413 Payload Too Large · 414 URI Too Long · 500 Internal Server Error · 501 Not Implemented · 503 Service Unavailable · others → "Unknown"

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
Location · SetCookie · ETag · Vary · Date · Server
AccessControlAllowOrigin · AccessControlAllowMethods · AccessControlAllowHeaders
AccessControlMaxAge · AccessControlExposeHeaders
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

### `AnkaOutOfRangeException`

Derives from `ArgumentOutOfRangeException`. Thrown when a numeric argument is outside its valid range — port outside 1–65535, or `ServerOptions.MaxRequestBodySize` / `ServerOptions.MaxRequestTargetSize` / `ServerOptions.MaxRequestHeadersSize` set to a negative value.

---

### `ServerOptions`

```
Namespace: Anka
Access:    public sealed
```

Optional configuration passed to the `Server` constructor. All properties are optional; when `null`, the server picks sensible defaults that scale with processor count.

| Property                  | Type                         | Default                          | Description                                                                                                     |
|---------------------------|------------------------------|----------------------------------|-----------------------------------------------------------------------------------------------------------------|
| `MinThreadPoolThreads`    | `int?`                       | `ProcessorCount * 2 + 2`         | Minimum worker/IO-completion threads for `ThreadPool`. Never overrides downward.                                |
| `AcceptorCount`           | `int?`                       | `max(ProcessorCount / 2, 2)`     | Number of parallel accept loops.                                                                                |
| `Backlog`                 | `int`                        | `512`                            | Backlog passed to `Socket.Listen()`.                                                                            |
| `DefaultResponseHeaders`  | `IReadOnlyList<HttpHeader>`  | `[]`                             | Headers appended to every response (e.g., security headers). Allocated once at startup — zero per-request cost. |
| `MaxRequestBodySize`      | `int?`                       | `null` (unlimited)               | Maximum allowed request body in bytes. Requests that exceed this limit automatically receive `413 Payload Too Large`. |
| `MaxRequestTargetSize`    | `int?`                       | `null` (unlimited)               | Maximum allowed request-target size in bytes. Requests that exceed this limit automatically receive `414 URI Too Long`. |
| `MaxRequestHeadersSize`   | `int`                        | `8192`                           | Maximum allowed aggregate size of request header names and values. Requests that exceed this limit, or the built-in header-count cap, automatically receive `431 Request Header Fields Too Large`. |
| `ReadTimeout`             | `TimeSpan?`                  | `null`                           | Optional idle read timeout used to close stalled connections and mitigate Slowloris-style requests. |

**Example:**

```csharp
var options = new ServerOptions
{
    AcceptorCount        = 4,
    MaxRequestBodySize   = 1 * 1024 * 1024,  // 1 MB
    MaxRequestTargetSize = 8 * 1024,         // 8 KB
    MaxRequestHeadersSize = 8 * 1024,        // 8 KB
    ReadTimeout = TimeSpan.FromSeconds(15),
    DefaultResponseHeaders =
    [
        new HttpHeader("x-content-type-options"u8.ToArray(), "nosniff"u8.ToArray()),
        new HttpHeader("x-frame-options"u8.ToArray(),        "DENY"u8.ToArray()),
    ]
};
```

---

### `HttpHeader`

```
Namespace: Anka
Access:    public readonly struct
```

A name/value pair for HTTP response headers. Create instances once at startup and store in `static readonly` arrays for zero per-request allocation.

```csharp
// Byte-based (zero allocation at call time — preferred for hot paths)
new HttpHeader("x-custom-header"u8.ToArray(), "value"u8.ToArray())

// String-based (allocates — use at startup only)
new HttpHeader("x-custom-header", "value")
```

| Member  | Type                    | Description                          |
|---------|-------------------------|--------------------------------------|
| `Name`  | `ReadOnlyMemory<byte>`  | Header name as lowercase ASCII bytes |
| `Value` | `ReadOnlyMemory<byte>`  | Header value as ASCII/UTF-8 bytes    |

---

### `ResponseContext`

```
Namespace: Anka
Access:    public readonly struct
```

A fluent builder for attaching extra per-request response headers. Obtained via `HttpResponseWriter.AddHeader(...)` (see `HttpResponseWriterExtensions`).

```csharp
await response
    .AddHeader(HttpHeaderNames.Location, "/new-path"u8)
    .WriteAsync(301, default, default, keepAlive: false, ct);

await response
    .AddHeader(HttpHeaderNames.AccessControlAllowOrigin, "*"u8)
    .AddHeader(HttpHeaderNames.AccessControlAllowMethods, "GET, POST"u8)
    .WriteAsync(200, body, "application/json; charset=utf-8"u8, keepAlive: true, ct);
```

> For zero-allocation hot paths, pass a `static readonly HttpHeader[]` directly to `WriteAsync` instead of using the fluent API (which allocates a `List<T>` per call).

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
│   ├── anka-logo.png                (package icon)
│   └── src/
│       ├── Core/                  ← Public API
│       │   ├── HttpHeader.cs        (name/value pair struct for response headers)
│       │   ├── HttpHeaderNames.cs   (pre-defined header name constants)
│       │   ├── HttpHeaders.cs       (zero-alloc header struct, InlineArray)
│       │   ├── HttpMethod.cs        (enum: byte)
│       │   ├── HttpRequest.cs       (parsed request + buffer ownership)
│       │   ├── HttpResponseWriter.cs(response writer, ArrayPool)
│       │   ├── HttpResponseWriterExtensions.cs (fluent AddHeader API)
│       │   ├── HttpVersion.cs       (enum: byte)
│       │   ├── RequestHandler.cs    (delegate definition)
│       │   ├── ResponseContext.cs   (fluent header builder)
│       │   ├── Server.cs            (public entry point)
│       │   └── ServerOptions.cs     (optional server configuration)
│       ├── Internal/              ← Implementation details (internal)
│       │   ├── Connection.cs        (socket lifecycle + sliding receive window)
│       │   ├── HttpMethodParser.cs  (byte span → HttpMethod enum)
│       │   ├── HttpParser.cs        (two-phase HTTP/1.x parser)
│       │   ├── HttpRequestPool.cs   (CAS single-slot object pool)
│       │   ├── HttpVersionParser.cs (byte span → HttpVersion enum)
│       │   └── SocketReceiver.cs    (zero-alloc SocketAsyncEventArgs + IValueTaskSource)
│       └── Exceptions/
│           ├── AnkaArgumentException.cs
│           └── AnkaOutOfRangeException.cs
│
├── Anka.Console/                  ← Sample application
│   └── Program.cs                   (Hello World server on :8080)
│
├── Anka.Test/                     ← xUnit tests
│   ├── ContentLengthValidationTests.cs
│   ├── CustomResponseHeaderTests.cs
│   ├── HttpHeadersTests.cs
│   ├── HttpMethodParserTests.cs
│   ├── HttpParserTests.cs
│   ├── HttpRequestTests.cs
│   ├── HttpVersionParserTests.cs
│   ├── RequestBodySizeLimitTests.cs
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
