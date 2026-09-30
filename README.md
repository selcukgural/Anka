# Anka

[![NuGet](https://img.shields.io/nuget/vpre/Anka.svg)](https://www.nuget.org/packages/Anka)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Anka.svg)](https://www.nuget.org/packages/Anka)
[![ci](https://github.com/selcukgural/Anka/actions/workflows/ci.yml/badge.svg)](https://github.com/selcukgural/Anka/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/selcukgural/Anka/blob/main/LICENSE)
[![.NET 8+](https://img.shields.io/badge/.NET-8.0%2B-512BD4)](https://dotnet.microsoft.com)

A minimal HTTP/1.1 server library for .NET 8+, built for **Native AOT**: the process is ready to accept
connections about **2 ms** after launch, uses about **15 MB** of memory, and allocates nothing per request on
keep-alive connections.

> ⚠️ **Beta: for research and experimentation only.** The API can still change between versions and the server has
> not been hardened for production traffic. Do not expose it directly to the internet.

## Contents

- [Is Anka a fit?](#is-anka-a-fit)
- [Getting started](#getting-started)
- [Core concepts](#core-concepts)
- [Recipes](#recipes)
- [Configuration](#configuration)
- [What Anka handles for you](#what-anka-handles-for-you)
- [Deployment](#deployment)
- [Writing fast handlers](#writing-fast-handlers)
- [Troubleshooting](#troubleshooting)
- [API reference](#api-reference)
- [Limitations](#limitations)
- [Contributing](#contributing)

## Is Anka a fit?

Anka is one `Server` class and one handler delegate. There is no middleware, routing, dependency injection or
configuration system. You get the parsed request and a response writer, and everything else is plain C#.

| Good fit | Not a fit |
|---|---|
| Serverless functions and short-lived containers where cold start matters | Product APIs with many endpoints, auth, versioning, OpenAPI |
| Sidecars, health/metrics endpoints, webhooks, internal callbacks | Apps that depend on the ASP.NET Core middleware ecosystem |
| Edge and memory-constrained deployments | HTTP/2, HTTP/3, WebSockets, built-in TLS |
| Learning or measuring HTTP/1.1 without framework noise | Production traffic today (beta) |

Measured against Kestrel on the same machine (Apple M3 Max, Native AOT vs JIT):

| | Anka | Kestrel |
|---|---:|---:|
| Time from process start to accepting connections | **2.3 ms** | 140 ms |
| Memory (RSS) after the first response | **15 MB** | 98 MB |
| Allocation during startup | **124.5 KB** | 2.5 MB |
| Plain-text throughput (wrk, 400 connections) | 133k req/s | 142k req/s |

Throughput is on par; the gains are startup time and footprint. Full numbers and methodology:
[docs/performance.md](https://github.com/selcukgural/Anka/blob/main/docs/performance.md).

## Getting started

### 1. Create a project

```bash
dotnet new console -n HelloAnka
cd HelloAnka
dotnet add package Anka --prerelease
```

`--prerelease` is required while Anka is in beta. Add Native AOT to `HelloAnka.csproj`:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net8.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

### 2. Write the server

`Program.cs`:

```csharp
using System.Runtime.InteropServices;
using Anka;

byte[] hello     = "Hello from Anka!"u8.ToArray();
byte[] notFound  = "Not Found"u8.ToArray();
byte[] textPlain = "text/plain; charset=utf-8"u8.ToArray();

using var cts = new CancellationTokenSource();
// Ctrl+C locally, SIGTERM from `docker stop` or Kubernetes.
using var sigint  = PosixSignalRegistration.Create(PosixSignal.SIGINT,  ctx => { ctx.Cancel = true; cts.Cancel(); });
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });

var server = new Server(
    handler: (req, res, ct) =>
    {
        if (req.Method is RequestMethod.Get or RequestMethod.Head && req.PathEquals("/"u8))
        {
            return res.WriteAsync(200, hello, textPlain, req.IsKeepAlive, ct);
        }

        return res.WriteAsync(404, notFound, textPlain, req.IsKeepAlive, ct);
    },
    port: 8080);

await server.StartAsync(cts.Token); // returns after cts is cancelled and open requests have finished
```

### 3. Run it

```bash
dotnet run
curl http://127.0.0.1:8080/        # Hello from Anka!
```

### 4. Publish as a native binary

```bash
dotnet publish -c Release -r osx-arm64 -o out   # or linux-x64, linux-arm64, win-x64 ...
./out/HelloAnka
```

The output is a single self-contained executable (about 2.5 MB for this example) that needs no .NET runtime
on the target machine. Native AOT needs a platform linker:
[prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites) (Xcode command-line
tools on macOS, `clang` and `zlib1g-dev` on Debian/Ubuntu, the C++ workload of Visual Studio on Windows).

Anka itself produces no trim or AOT warnings. If `dotnet publish` prints `IL2xxx` or `IL3xxx` warnings, they come
from your code or another dependency; take them seriously, because code that warns can fail at runtime only in
the published binary.

## Core concepts

### One handler

```csharp
public delegate ValueTask RequestHandler(HttpRequest request, HttpResponseWriter response, CancellationToken cancellationToken);
```

Every request on every connection calls the same handler. Dispatch is ordinary code: `if`, `switch`, a
dictionary, or a [source generator](https://github.com/selcukgural/anka-source-generated-routing).

### Bytes, not strings

The request exposes raw bytes (`PathBytes`, `QueryBytes`, header values as `ReadOnlySpan<byte>`) and the
response takes bytes (`ReadOnlyMemory<byte>`). This is what keeps the hot path allocation-free.

- Create constant bodies and content types **once**, as `static readonly byte[]` fields or locals captured by the
  handler, with UTF-8 literals: `"text/plain"u8.ToArray()`.
- A bare `"..."u8` literal is a `ReadOnlySpan<byte>` and cannot be passed where `ReadOnlyMemory<byte>` is
  expected, so `res.WriteAsync(200, body, "text/plain"u8, ...)` does not compile. Store the bytes first.
- Convenience properties that allocate exist when you need them: `req.Path`, `req.QueryString`.

### Exactly one response per request

Call **one** of `WriteAsync`, `WritePartialAsync`, `StartChunkedResponseAsync` or `GetStream()` per request.
Starting a second response throws `InvalidOperationException`; check `res.HasStarted` if a code path may already
have written. When the handler returns:

| Handler outcome | What Anka sends |
|---|---|
| Wrote a response | That response |
| Wrote nothing | `200 OK` with an empty body |
| Started a chunked response and did not finish it | The terminating chunk |
| Threw before writing | `500 Internal Server Error`, `Connection: close`; the exception type and message go to stderr |
| Threw after writing started | Nothing more; the connection is closed |

### Keep-alive

Pass `req.IsKeepAlive` as the `keepAlive` argument. HTTP/1.1 connections stay open unless the client sends
`Connection: close`; HTTP/1.0 connections close unless the client sends `Connection: keep-alive`. Passing `false`
closes the connection after the response; passing `true` cannot override a client that asked to close.

### The request object is reused

`HttpRequest`, its headers and `req.Body` point into buffers that are recycled for the next request on the same
connection. Use them freely inside the handler (including across `await`), but **copy anything you need after
the handler returns**, e.g. before handing work to a background task: `req.Body.ToArray()`, `req.Path`.

### Cancellation

The `CancellationToken` passed to the handler is cancelled when a shutdown runs out of time (see
[Shutdown behaviour](#shutdown-behaviour)), not when a client disconnects. Pass it to your own I/O (database
calls, `HttpClient`) so a stuck call cannot hold up the process.

## Recipes

All snippets are taken from a sample that is published with Native AOT and exercised with `curl`. The helpers
used below (`TextPlain`, `Json`) are:

```csharp
static readonly byte[] TextPlain = "text/plain; charset=utf-8"u8.ToArray();
static readonly byte[] Json      = "application/json"u8.ToArray();
```

### Routing

```csharp
static ValueTask HandleAsync(HttpRequest req, HttpResponseWriter res, CancellationToken ct)
{
    if (req.Method == RequestMethod.Options)
        return res.WriteAsync(204, default, default, req.IsKeepAlive, CorsHeaders, ct);

    if (req.Method is RequestMethod.Get or RequestMethod.Head) // HEAD: Anka sends the headers and drops the body
    {
        if (req.PathEquals("/"u8))                  return res.WriteAsync(200, Hello, TextPlain, req.IsKeepAlive, ct);
        if (req.PathEquals("/search"u8))            return Search(req, res, ct);
        if (req.PathBytes.StartsWith("/users/"u8))  return GetUser(req, res, ct);
    }

    if (req.Method == RequestMethod.Post && req.PathEquals("/users"u8))
        return CreateUser(req, res, ct);

    return res.WriteAsync(404, NotFound, TextPlain, req.IsKeepAlive, ct);
}
```

`PathEquals` compares bytes without allocating. Handle `HEAD` together with `GET`; otherwise `HEAD` requests fall
through to your 404.

### Path parameters

```csharp
// GET /users/42
static ValueTask GetUser(HttpRequest req, HttpResponseWriter res, CancellationToken ct)
{
    var idBytes = req.PathBytes["/users/".Length..];
    if (!Utf8Parser.TryParse(idBytes, out int id, out var consumed) || consumed != idBytes.Length)
        return res.WriteAsync(400, "invalid id"u8.ToArray(), TextPlain, req.IsKeepAlive, ct);

    var body = JsonSerializer.SerializeToUtf8Bytes(new User(id, $"user-{id}"), AppJson.Default.User);
    return res.WriteAsync(200, body, Json, req.IsKeepAlive, ct);
}
```

`Utf8Parser` lives in `System.Buffers.Text`.

### Query string

Anka exposes the raw query (`req.QueryBytes`, or `req.QueryString` as a string) but does not split or
percent-decode it. A small zero-allocation helper:

```csharp
static class Query
{
    /// <summary>Finds <paramref name="key"/> in a raw query string ("a=1&b=2"). Does not percent-decode.</summary>
    public static bool TryGet(ReadOnlySpan<byte> query, ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        while (!query.IsEmpty)
        {
            var amp  = query.IndexOf((byte)'&');
            var pair = amp < 0 ? query : query[..amp];
            var eq   = pair.IndexOf((byte)'=');
            var name = eq < 0 ? pair : pair[..eq];
            if (name.SequenceEqual(key))
            {
                value = eq < 0 ? default : pair[(eq + 1)..];
                return true;
            }

            query = amp < 0 ? default : query[(amp + 1)..];
        }

        value = default;
        return false;
    }
}

// GET /search?q=anka&limit=10
static ValueTask Search(HttpRequest req, HttpResponseWriter res, CancellationToken ct)
{
    var q     = Query.TryGet(req.QueryBytes, "q"u8, out var qv) ? Encoding.UTF8.GetString(qv) : "";
    var limit = Query.TryGet(req.QueryBytes, "limit"u8, out var lv) && Utf8Parser.TryParse(lv, out int n, out _) ? n : 20;

    return res.WriteAsync(200, Encoding.UTF8.GetBytes($"q={q} limit={limit}"), TextPlain, req.IsKeepAlive, ct);
}
```

If values can contain `%XX` escapes or `+`, decode them (e.g. `Uri.UnescapeDataString`) after extracting.

### Request headers

Header names are stored in lowercase. Use the `HttpHeaderNames` constants, or a lowercase UTF-8 literal:

```csharp
var userAgent = req.Headers.TryGetValue(HttpHeaderNames.UserAgent, out var ua)
    ? Encoding.ASCII.GetString(ua)   // values are bytes; convert only when you need a string
    : "unknown";

var custom = req.Headers.TryGetValue("x-request-id"u8, out var id);          // name must be lowercase
var byName = req.Headers.TryGetValue("X-Request-Id", out var id2);           // string overload lowercases for you

// A header sent more than once
if (req.Headers.TryGetAllValues(HttpHeaderNames.Accept, out var values))
{
    foreach (var value in values) { /* each value is a ReadOnlySpan<byte> */ }
}
```

### JSON with System.Text.Json

Reflection-based `JsonSerializer` calls do not work under Native AOT. Use a source-generated context:

```csharp
record User(int Id, string Name);
record CreateUserRequest(string? Name);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(CreateUserRequest))]
partial class AppJson : JsonSerializerContext;

// POST /users  {"name":"anka"}
static ValueTask CreateUser(HttpRequest req, HttpResponseWriter res, CancellationToken ct)
{
    CreateUserRequest? input;
    try
    {
        input = JsonSerializer.Deserialize(req.Body.Span, AppJson.Default.CreateUserRequest);
    }
    catch (JsonException)
    {
        return res.WriteAsync(400, "invalid json"u8.ToArray(), TextPlain, req.IsKeepAlive, ct);
    }

    if (string.IsNullOrWhiteSpace(input?.Name))
        return res.WriteAsync(422, "name is required"u8.ToArray(), TextPlain, req.IsKeepAlive, ct);

    var body = JsonSerializer.SerializeToUtf8Bytes(new User(1, input.Name), AppJson.Default.User);
    return res.WriteAsync(201, body, Json, req.IsKeepAlive, ct);
}
```

`req.Body` is the complete body: Anka reads `Content-Length` and chunked bodies fully before calling the
handler, up to `MaxRequestBodySize` (30 MB by default; larger requests get `413`).

### Response headers

Build header sets that are the same on every response **once**:

```csharp
static readonly HttpHeader[] CacheHeaders =
[
    new HttpHeader(HttpHeaderNames.CacheControl.ToArray(), "max-age=60"u8.ToArray()),
    new HttpHeader("x-powered-by"u8.ToArray(), "anka"u8.ToArray()),
];

return res.WriteAsync(200, body, Json, req.IsKeepAlive, CacheHeaders, ct);
```

For occasional per-request headers the fluent API is shorter but allocates a list per call:

```csharp
return res.AddHeader(HttpHeaderNames.Location, "/new-path"u8)
          .WriteAsync(301, keepAlive: req.IsKeepAlive, cancellationToken: ct);
```

Headers that go on **every** response (security headers, branding) belong in `ServerOptions.DefaultResponseHeaders`:

```csharp
var options = new ServerOptions
{
    DefaultResponseHeaders =
    [
        new HttpHeader("x-content-type-options"u8.ToArray(), "nosniff"u8.ToArray()),
        new HttpHeader("referrer-policy"u8.ToArray(), "no-referrer"u8.ToArray()),
    ],
};
```

Header names and values, and the `contentType` argument, are validated: CR, LF and other control characters
throw `ArgumentException`, so user input cannot inject extra headers. Use lowercase names.

### CORS

```csharp
static readonly HttpHeader[] CorsHeaders =
[
    new HttpHeader(HttpHeaderNames.AccessControlAllowOrigin.ToArray(),  "*"u8.ToArray()),
    new HttpHeader(HttpHeaderNames.AccessControlAllowMethods.ToArray(), "GET, POST, OPTIONS"u8.ToArray()),
    new HttpHeader(HttpHeaderNames.AccessControlAllowHeaders.ToArray(), "content-type"u8.ToArray()),
    new HttpHeader(HttpHeaderNames.AccessControlMaxAge.ToArray(),       "600"u8.ToArray()),
];

// Preflight
if (req.Method == RequestMethod.Options)
    return res.WriteAsync(204, default, default, req.IsKeepAlive, CorsHeaders, ct);

// Actual responses: pass CorsHeaders as extra headers, or put Access-Control-Allow-Origin in DefaultResponseHeaders.
```

### Streaming responses

`GetStream()` returns a `Stream` that sends `Transfer-Encoding: chunked`; the status line and headers go out on
the first write and the terminating chunk on dispose. It always answers `200` with no `Content-Type`:

```csharp
static async ValueTask StreamAsync(HttpRequest req, HttpResponseWriter res, CancellationToken ct)
{
    await using var stream = (HttpResponseStream)res.GetStream(ct);
    for (var i = 1; i <= 3; i++)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"line {i}\n"), ct);
    }

    stream.AddTrailer(new HttpHeader("x-line-count"u8.ToArray(), "3"u8.ToArray())); // optional trailers
}
```

For another status code, a content type or extra headers, use the chunk API directly:

```csharp
await res.StartChunkedResponseAsync(200, NdJson, req.IsKeepAlive, extraHeaders, ct);
await res.WriteChunkAsync(chunk1, ct);
await res.WriteChunkAsync(chunk2, ct);
await res.FinishChunkedResponseAsync(trailers: default, ct);
```

For HTTP/1.0 clients, which have no chunked encoding, both APIs write the body unframed and close the
connection afterwards; trailers are dropped.

### Range requests

`req.TryGetRange(length, out start, out end)` reads the `Range` header and resolves `bytes=0-99`, `bytes=100-`
and `bytes=-50` against the length of your content. `WritePartialAsync` then sends `206 Partial Content` with
`Content-Range`:

```csharp
// GET /doc
if (req.TryGetRange(Document.Length, out var start, out var end))
{
    var slice = Document.AsMemory((int)start, (int)(end - start + 1));
    return res.WritePartialAsync(start, end, Document.Length, slice, TextPlain, req.IsKeepAlive, DocumentHeaders, ct);
}

return res.WriteAsync(200, Document, TextPlain, req.IsKeepAlive, DocumentHeaders, ct);
```

`TryGetRange` returns `false` for requests other than `GET`, for multi-range or malformed values and for ranges
that start past the end.
In those cases serve the full `200` response, as above. Every `200` response
carries `Accept-Ranges: bytes`.

### Conditional requests (ETag → 304)

Put an `ETag` header on a `200` response. When the request's `If-None-Match` equals it byte for byte, Anka
turns the response into `304 Not Modified` without a body:

```csharp
static readonly HttpHeader[] DocumentHeaders =
[
    new HttpHeader(HttpHeaderNames.ETag.ToArray(), "\"v1\""u8.ToArray()),
    new HttpHeader(HttpHeaderNames.CacheControl.ToArray(), "max-age=60"u8.ToArray()),
];
```

Only an exact single-value match is recognised; lists (`"a", "b"`), `*` and weak comparison (`W/`) are not.
Compare `If-None-Match` yourself for those.

### File uploads (multipart/form-data)

`MultipartParser` splits a body into parts; `MultipartParser.TryGetBoundary` reads the boundary from
`Content-Type`:

```csharp
static ValueTask Upload(HttpRequest req, HttpResponseWriter res, CancellationToken ct)
{
    if (!req.Headers.TryGetValue(HttpHeaderNames.ContentType, out var contentType) ||
        !MultipartParser.TryGetBoundary(contentType, out var boundary))
    {
        return res.WriteAsync(415, default, default, req.IsKeepAlive, ct);
    }

    var parser = new MultipartParser(new ReadOnlySequence<byte>(req.Body), boundary);
    while (parser.TryReadNextPart(out var part))
    {
        if (part.TryGetContentDisposition(out var name, out var fileName))
        {
            // name / fileName / part.Content are ReadOnlySequence<byte> slices of req.Body
        }
    }

    return res.WriteAsync(204, default, default, req.IsKeepAlive, ct);
}
```

The whole upload is buffered in memory first, so size `MaxRequestBodySize` for your largest file. Treat file names
as untrusted input.

### Readiness and startup metrics

`ListeningStarted` fires once the socket is bound and accepting:

```csharp
server.ListeningStarted += endpoint => Console.WriteLine($"ready on {endpoint}");
```

Anka writes nothing to standard output. The only thing it logs is an unhandled handler exception (type and
message) on standard error.

## Configuration

Pass a `ServerOptions` to the constructor. Every property is optional.

```csharp
var server = new Server(handler, port: 8080, host: "0.0.0.0", options: new ServerOptions
{
    MaxRequestBodySize       = 1 * 1024 * 1024,
    MaxRequestTargetSize     = 8 * 1024,
    ReadTimeout              = TimeSpan.FromSeconds(15),
    MaxConcurrentConnections = 10_000,
});
```

| `Server` constructor | Default | Notes |
|---|---|---|
| `port` | — | 1–65535, otherwise `AnkaOutOfRangeException` |
| `host` | `"127.0.0.1"` | An IP literal. `"0.0.0.0"` listens on all IPv4 interfaces (required in containers); `"::"` listens on all interfaces, IPv6 and IPv4 (dual-stack); `"::1"` is IPv6 loopback. Host names such as `localhost` are not resolved; invalid values throw `AnkaArgumentException`. |

| `ServerOptions` | Default | Effect |
|---|---|---|
| `MaxRequestBodySize` | 30,000,000 bytes | Larger bodies (declared or chunked) get `413`. `null` removes the limit; bodies are buffered in memory, so only do that behind a proxy that limits them. |
| `MaxRequestTargetSize` | unlimited | Longer request-targets (path + query) get `414`. |
| `MaxRequestHeadersSize` | 8 KB | Total bytes of header names + values; more gets `431`. Also capped internally at ~64 KB together with the request-target. |
| `ReadTimeout` | 30 s | Closes a connection that sends nothing for this long, including idle keep-alive connections. `null` disables. |
| `RequestHeadersTimeout` | 30 s | Absolute deadline for the complete request line and headers, from the first byte. Stops slow-header (Slowloris) clients. `null` disables. |
| `MaxConcurrentConnections` | unlimited | Connections above the limit are closed immediately without a response. |
| `ShutdownTimeout` | 10 s | How long a shutdown waits for requests already in the handler; see [Shutdown behaviour](#shutdown-behaviour). |
| `DefaultResponseHeaders` | none | Headers added to every response. |
| `AcceptorCount` | `max(ProcessorCount / 2, 2)` | Parallel accept loops. |
| `Backlog` | 512 | Listen backlog. |
| `MinThreadPoolThreads` | `ProcessorCount * 2 + 2` | Raised at startup to avoid slow thread injection under bursts; never lowered. |

Negative sizes or timeouts, and `MaxConcurrentConnections < 1`, throw `AnkaOutOfRangeException`.

## What Anka handles for you

Before your handler runs, Anka validates the request and answers these cases itself (the connection is closed
afterwards):

| Status | When |
|---|---|
| `400 Bad Request` | Malformed request line or headers, unknown method, control characters in the target or a header, header line without a colon, obsolete line folding, invalid or conflicting `Content-Length`, both `Content-Length` and `Transfer-Encoding`, unsupported transfer coding, malformed chunked body, missing / duplicate / invalid `Host` on HTTP/1.1 |
| `411 Length Required` | `POST`, `PUT` or `PATCH` without `Content-Length` or `Transfer-Encoding` |
| `413 Payload Too Large` | Body exceeds `MaxRequestBodySize` |
| `414 URI Too Long` | Request-target exceeds `MaxRequestTargetSize` |
| `431 Request Header Fields Too Large` | Headers exceed `MaxRequestHeadersSize` or there are more than 64 of them |
| `505 HTTP Version Not Supported` | Well-formed but unsupported version, e.g. `HTTP/2.0` |

And during a request:

- **`Expect: 100-continue`** is answered with `100 Continue` before the body is read.
- **Chunked request bodies** are decoded into `req.Body`; trailer fields are in `req.Trailers`.
- **Pipelined requests** on one connection are processed in order.
- **`HEAD`** responses keep their headers (including `Content-Length`) and drop the body.
- **`204`, `304` and `1xx`** responses are sent without body and without `Content-Length` / `Content-Type`.
- **Every response** carries `Server: Anka`, `Date`, `Connection` and, unless chunked or body-less,
  `Content-Length`. `200` responses also carry `Accept-Ranges: bytes`.
- **The response version** follows the request: HTTP/1.0 requests get `HTTP/1.0` responses.
- **Status lines** use the standard reason phrase for every code registered in RFC 9110 plus 102, 103, 207,
  208, 226, 423, 424, 428, 429, 431, 451, 506–508 and 511. Other codes are sent with an empty reason phrase,
  which RFC 9112 allows. Status codes below 200 or above 999 throw `ArgumentOutOfRangeException`: 1xx codes are
  interim responses and cannot be the final one.

Request-targets in origin form (`/path?q`), absolute form (`http://host/path`), authority form (`CONNECT
host:port`) and asterisk form (`OPTIONS *`) are all accepted; an absolute-form target must agree with `Host`.

## Deployment

### Always behind a reverse proxy

Anka has no TLS. Put it behind nginx, Caddy, Envoy, a cloud load balancer or an ingress controller that
terminates TLS and forwards HTTP/1.1. Keep the limits in the proxy at least as strict as Anka's.

### Container image

A multi-stage build compiles the native binary and ships it on a minimal, non-root base image:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore --use-current-runtime
COPY . .
RUN dotnet publish -c Release --use-current-runtime -o /app

# No .NET runtime needed: the Native AOT binary only depends on libc & co. Chiseled images run as non-root.
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-noble-chiseled
WORKDIR /app
COPY --from=build /app/HelloAnka .
EXPOSE 8080
ENTRYPOINT ["./HelloAnka"]
```

Listen on `host: "0.0.0.0"` inside the container; the default `127.0.0.1` is not reachable through a published
port. With the `SIGTERM` handler from [Getting started](#2-write-the-server), `docker stop` returns immediately.
The resulting image is about 15 MB.

### Shutdown behaviour

Cancelling the token passed to `StartAsync` starts a graceful shutdown:

1. The listener closes; no new connections are accepted.
2. Connections that are not running a handler (idle keep-alive connections, requests still being received) are
   closed right away.
3. Requests already inside the handler may finish; their responses carry `Connection: close`.
4. After `ServerOptions.ShutdownTimeout` (10 s by default) the handler's `CancellationToken` is cancelled and
   the remaining connections are closed.

`StartAsync` returns once every connection has ended. Keep `ShutdownTimeout` below the platform's grace period
(Kubernetes waits 30 s by default before `SIGKILL`); `TimeSpan.Zero` aborts in-flight requests immediately.

## Writing fast handlers

Anka's own hot path allocates nothing; whether your service does depends on the handler.

- Keep constant bodies, content types and header arrays in `static readonly` fields.
- Prefer `PathEquals` / `PathBytes` / `QueryBytes` over `Path` / `QueryString`, which create strings.
- Prefer a `static readonly HttpHeader[]` over `AddHeader(...)`, which allocates a list per call.
- Serialize JSON with a source-generated `JsonSerializerContext`; `SerializeToUtf8Bytes` allocates the result
  array, so write directly into a pooled buffer if that matters.
- Return the `ValueTask` from `WriteAsync` directly when there is nothing else to await; `async` handlers pay for
  a state machine when they complete asynchronously.
- Measure with `dotnet-counters` or BenchmarkDotNet's `[MemoryDiagnoser]` rather than guessing.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `CS1503: cannot convert from 'System.ReadOnlySpan<byte>' to 'System.ReadOnlyMemory<byte>'` | A `"..."u8` literal was passed as body or content type. Store it first: `static readonly byte[] X = "..."u8.ToArray();`. |
| Works locally, not reachable from Docker / another machine | The server listens on `127.0.0.1` by default. Pass `host: "0.0.0.0"`. |
| `HEAD` requests return 404 | Routing only checks `RequestMethod.Get`; match `Get or Head`. |
| `InvalidOperationException: The response has already started` | Two responses were written for one request. Return after the first, or check `res.HasStarted`. |
| JSON works with `dotnet run` but fails in the published binary | Reflection-based `JsonSerializer` overloads; use a `JsonSerializerContext` (see [JSON](#json-with-systemtextjson)). Publish once and read the `IL2026` / `IL3050` warnings. |
| Garbage data in a background task | `req.Body` or header spans were used after the handler returned. Copy them first. |

## API reference

All public types are in the `Anka` namespace unless noted.

### `Server`

| Member | Description |
|---|---|
| `Server(RequestHandler handler, int port, string host = "127.0.0.1", ServerOptions? options = null)` | See [Configuration](#configuration). |
| `Task StartAsync(CancellationToken cancellationToken = default)` | Binds and serves until the token is cancelled, then shuts down gracefully and returns. |
| `event Action<IPEndPoint>? ListeningStarted` | Raised once the socket is accepting connections. |

### `HttpRequest`

| Member | Type | Description |
|---|---|---|
| `Method` | `RequestMethod` | `Get`, `Head`, `Post`, `Put`, `Delete`, `Patch`, `Options`, `Trace`, `Connect` |
| `Version` | `HttpVersion` | `Http10` or `Http11` |
| `PathBytes` | `ReadOnlySpan<byte>` | Path without the query, not percent-decoded |
| `PathEquals(ReadOnlySpan<byte>)` | `bool` | Allocation-free path comparison |
| `Path` | `string` | ASCII string of `PathBytes`, created on first access |
| `QueryBytes` | `ReadOnlySpan<byte>` | Text after `?`, empty if none |
| `QueryString` | `string?` | ASCII string of `QueryBytes`, `null` if there is no query |
| `Headers` | `HttpHeaders` | Request headers |
| `Trailers` | `HttpHeaders` | Trailer fields of a chunked request body |
| `Body` | `ReadOnlyMemory<byte>` | Complete request body; empty if none |
| `IsKeepAlive` | `bool` | Whether the connection should stay open |

### `HttpHeaders` (struct)

| Member | Description |
|---|---|
| `bool TryGetValue(ReadOnlySpan<byte> lowercaseName, out ReadOnlySpan<byte> value)` | First value of a header. The name must be lowercase. |
| `bool TryGetValue(string name, out ReadOnlySpan<byte> value)` | Same, any casing (names up to 128 characters). |
| `bool TryGetAllValues(ReadOnlySpan<byte> lowercaseName, out HeaderValues values)` | All values of a repeated header; enumerate with `foreach`. |
| `int Count` | Number of header fields. |

`HttpHeaderNames` provides lowercase names as `ReadOnlySpan<byte>`: `Host`, `Connection`, `ContentLength`,
`ContentType`, `TransferEncoding`, `Accept`, `AcceptEncoding`, `Authorization`, `UserAgent`, `CacheControl`,
`Cookie`, `Expect`, `Range`, `IfRange`, `IfMatch`, `IfNoneMatch`, `IfModifiedSince`, `IfUnmodifiedSince`, `Origin`,
`Referer`, `Location`, `SetCookie`, `ETag`, `LastModified`, `Vary`, `WwwAuthenticate`, `Allow`, `RetryAfter`,
`ContentRange`, `AcceptRanges`, `AccessControlAllowOrigin`, `AccessControlAllowMethods`,
`AccessControlAllowHeaders`, `AccessControlMaxAge`, `AccessControlExposeHeaders` (and the value `Chunked`).
Use `.ToArray()` when you need them as `HttpHeader` names.

### `HttpResponseWriter`

| Member | Description |
|---|---|
| `WriteAsync(int statusCode, ReadOnlyMemory<byte> body = default, ReadOnlyMemory<byte> contentType = default, bool keepAlive = true, CancellationToken ct = default)` | Complete response with `Content-Length`. Bodies up to 4 KB go out in the same send as the headers. |
| `WriteAsync(int statusCode, ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> contentType, bool keepAlive, ReadOnlySpan<HttpHeader> extraHeaders, CancellationToken ct = default)` | Same, with extra headers. |
| `WritePartialAsync(long rangeStart, long rangeEnd, long totalLength, ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> contentType, bool keepAlive = true, ReadOnlySpan<HttpHeader> extraHeaders = default, CancellationToken ct = default)` | `206 Partial Content` with `Content-Range: bytes start-end/total` (end inclusive). |
| `StartChunkedResponseAsync(int statusCode, ReadOnlyMemory<byte> contentType = default, bool keepAlive = true, ReadOnlySpan<HttpHeader> extraHeaders = default, CancellationToken ct = default)` | Starts a `Transfer-Encoding: chunked` response. |
| `WriteChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)` | Writes one chunk. Empty chunks are ignored. |
| `FinishChunkedResponseAsync(ReadOnlySpan<HttpHeader> trailers = default, CancellationToken ct = default)` | Terminating chunk plus optional trailers. |
| `Stream GetStream(CancellationToken ct = default)` | `HttpResponseStream` over the chunk API, `200` only. |
| `bool HasStarted` | `true` once a response has been started for the current request. |
| `AddHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)` / `AddHeader(string, string)` | Extension methods returning a `ResponseContext` with `AddHeader`, `WriteAsync` and `StartChunkedResponseAsync`. Allocates. |

Do not call `Dispose()`; the connection owns the writer.

### `HttpResponseStream`

A write-only `Stream` (`CanRead`/`CanSeek` are `false`). Additionally:

| Member | Description |
|---|---|
| `void AddTrailer(HttpHeader header)` | Trailer sent with the terminating chunk. |
| `ValueTask FinishAsync(CancellationToken ct = default)` | Sends the terminating chunk; also called by `DisposeAsync`. |

### `HttpHeader` (struct)

| Member | Description |
|---|---|
| `HttpHeader(ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value)` | No allocation. Throws `ArgumentException` for an empty or non-token name, or control characters in the value. |
| `HttpHeader(string name, string value)` | Converts to ASCII and lowercases the name. Allocates; use at startup. |
| `Name`, `Value` | `ReadOnlyMemory<byte>` |

### `MultipartParser` (`ref struct`)

| Member | Description |
|---|---|
| `static bool TryGetBoundary(ReadOnlySpan<byte> contentType, out ReadOnlySpan<byte> boundary)` | Boundary of a `multipart/form-data` Content-Type (quoted or token, 1–70 characters). |
| `MultipartParser(ReadOnlySequence<byte> body, ReadOnlySpan<byte> boundary)` | `boundary` without the leading `--`. |
| `bool TryReadNextPart(out MultipartPart part)` | Next part, or `false` at the closing boundary or on malformed input. |
| `MultipartPart.Headers`, `MultipartPart.Content` | `ReadOnlySequence<byte>` slices of the body. |
| `bool MultipartPart.TryGetContentDisposition(out ReadOnlySequence<byte> name, out ReadOnlySequence<byte> fileName)` | `name` and `filename` parameters (quoted or token). |

### Other types

| Type | Description |
|---|---|
| `RequestHandler` | `delegate ValueTask RequestHandler(HttpRequest request, HttpResponseWriter response, CancellationToken cancellationToken)` |
| `RequestMethod` | `enum : byte { Unknown, Get, Post, Put, Delete, Head, Options, Patch, Trace, Connect }` |
| `HttpVersion` | `enum : byte { Unknown, Http10, Http11 }` |
| `ServerOptions` | See [Configuration](#configuration). `DefaultMaxRequestBodySize`, `DefaultTimeout` and `DefaultShutdownTimeout` hold the defaults. |
| `AnkaArgumentException` | `ArgumentException` for an invalid host. |
| `AnkaOutOfRangeException` | `ArgumentOutOfRangeException` for an invalid port or option value. |

## Limitations

| | |
|---|---|
| HTTP/2, HTTP/3 | Not planned; HTTP/1.0 and HTTP/1.1 only. HTTP/0.9 is rejected. |
| TLS | Not built in; terminate at a reverse proxy. |
| Host names | `host` must be an IP literal; names such as `localhost` are not resolved. |
| WebSockets / `Upgrade` | Not supported. |
| Compression | No built-in `Content-Encoding`; compress or decompress in the handler. |
| Request bodies | Buffered in memory before the handler runs; no streaming request bodies. |
| Routing, middleware, DI, auth | Out of scope by design. |
| `If-Range`, `If-Modified-Since`, multi-range | Not handled automatically. |

## Contributing

```bash
dotnet build Anka.slnx --nologo
dotnet test Anka.slnx --nologo
dotnet run --project Benchmark/Anka.Benchmark -c Release   # hot paths must report 0 B allocated
```

- [docs/architecture.md](https://github.com/selcukgural/Anka/blob/main/docs/architecture.md): request
  lifecycle, components, memory model, repository layout.
- [docs/performance.md](https://github.com/selcukgural/Anka/blob/main/docs/performance.md): benchmark results and
  how to run the load tests.
- [AGENTS.md](https://github.com/selcukgural/Anka/blob/main/AGENTS.md): conventions that keep the code AOT-safe
  and allocation-free.

**CI.** `.github/workflows/ci.yml` runs on every push to `main` and every pull request: build and tests on Ubuntu
and macOS, a Native AOT publish of `Anka.HttpConsole` that fails on any `IL` warning followed by an HTTP smoke
test, and `dotnet pack`.

**Releases.** Bump `<Version>` and `<PackageReleaseNotes>` in `src/Anka/Anka.csproj`, merge to `main`, then push
a matching tag:

```bash
git tag v0.0.1-beta.6
git push origin v0.0.1-beta.6
```

`.github/workflows/release.yml` checks that the tag matches the project version and is on `main`, runs the tests,
publishes to nuget.org with [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
(OIDC, no stored API key) and creates a GitHub Release with the packages attached.

## License

[MIT](https://github.com/selcukgural/Anka/blob/main/LICENSE)
