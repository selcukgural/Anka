# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Anka is a zero-allocation HTTP/1.x server library for .NET 8+, built for Native AOT with sub-25ms cold starts and zero heap allocation in steady state (keep-alive request loop). It targets serverless/edge use cases: raw sockets, no middleware pipeline, no routing, no HTTP/2, no TLS — the caller's `RequestHandler` delegate does all dispatch. Status is beta (v0.0.1-beta.4) — research/experimentation, not production-hardened.

`AGENTS.md` in the repo root contains a longer-form architecture writeup (memory model, async safety rules, extension-point walkthroughs) — read it for depth beyond this file. Its "Strategic Roadmap" section and the README's RFC-compliance roadmap were updated on 2026-08-14 to mark Range Requests, `ETag`/`If-None-Match` cache validation, and multipart parsing as done (they previously lagged the code). No roadmap items are currently open in either doc — if you add a new one there, keep the "Supported" RFC tables in README in sync rather than letting the checklist drift again.

## Commands

```bash
# Build (all projects)
dotnet build Anka.slnx --nologo

# Full test suite
dotnet test Anka.slnx --nologo

# Single test by fully-qualified name
dotnet test Test/Anka.Test --nologo --filter "FullyQualifiedName~TryParse_SimpleGet_ReturnsRequest"

# Microbenchmarks (BenchmarkDotNet; run in Release, expect 0 B allocated)
dotnet run --project Benchmark/Anka.Benchmark -c Release

# Load testing (startup + throughput, wrk-based, compares vs Kestrel)
dotnet run --project Test/LoadTest/Anka.Wrk.LoadTest --configuration Release

# Linux load-test in Docker (arm64 runs natively on Apple Silicon, no emulation)
./scripts/run-linux-benchmark.sh
```

SDK is pinned via `global.json` to .NET 8.0.0 (`rollForward: latestMajor`).

## Architecture

### Request lifecycle (one loop per keep-alive TCP connection)

```
Server.StartAsync()
  → N parallel AcceptLoopAsync() (N = ServerOptions.AcceptorCount, default ProcessorCount/2)
    → Connection.RunAsync() — one Task per accepted socket, fire-and-forget from the accept loop
       1. SocketReceiver.ReceiveAsync()   — sync-completes on loopback, async (ThreadPool) on WAN
       2. HttpParser.TryParse()           — single-pass parse into pooled HttpRequest
       3. 100-continue sent if Expect header present, before body read
       4. body read: Content-Length slice, or ChunkedBodyParser for Transfer-Encoding: chunked
       5. RequestHandler(request, response, ct) — user delegate, awaited
       6. HttpResponseWriter.WriteAsync() — writes headers (+ small body inline) directly to the socket
       7. keep-alive check → loop back to step 1, or close
```

### Buffer/allocation model

- Per-connection, allocated once and reused: a 64 KB `ArrayPool` receive buffer (sliding window, compacted only when the tail fills), one `HttpRequest` from `HttpRequestPool` (a 32-slot CAS pool — deliberately not `ConcurrentQueue`, which allocates), and the `HttpResponseWriter`'s `ArrayPool` write buffer.
- Per-request: `HttpRequest.ResetForReuse()` reuses the same instance across requests on one connection; headers live in an inline fixed-size array inside the struct (no heap); the body is a `ReadOnlyMemory<byte>` slice into the request's rented body buffer.
- On connection close: `HttpRequestPool.Return` returns the request's body buffer to `ArrayPool` (the header buffer, ≤ 64 KB, stays with the pooled instance); `SocketReceiver`/`HttpResponseWriter` are disposed and the receive buffer is returned.
- Request bodies are buffered in memory before the handler runs, into a body buffer that grows as bytes arrive (never pre-sized from `Content-Length`). `ServerOptions` defaults are deliberately safe: `MaxRequestBodySize` 30 MB, `ReadTimeout` 30 s, `RequestHeadersTimeout` 30 s (absolute header deadline against Slowloris).
- Result: steady-state keep-alive traffic is zero-allocation. Any benchmark that shows nonzero allocated bytes is a regression — investigate before merging.

### Directory layout

- `src/Anka/src/Core/` — public API surface: `Server`, `ServerOptions`, `HttpRequest`, `HttpResponseWriter` (+ `HttpResponseWriterExtensions`, `ResponseContext`), `HttpResponseStream` (chunked response stream), `HttpHeaders`/`HttpHeader`/`HttpHeaderNames`, `HttpMethod`, `HttpVersion`, `RequestHandler` delegate.
- `src/Anka/src/Internal/` — implementation, `internal` visibility, exposed to tests/benchmarks only via `InternalsVisibleTo`: `Connection` (socket lifecycle), `HttpParser` (the parser itself), `HttpRequestPool`, `SocketReceiver`, `ChunkedBodyParser`, `MultipartParser`, `HttpMethodParser`, `HttpVersionParser`, `RequestTargetForm`.
- `src/Anka/src/Extensions/` — `HttpRequestExtensions` (helpers on the public `HttpRequest`).
- `src/Anka/src/Exceptions/` — `AnkaArgumentException`, `AnkaOutOfRangeException`.
- `Test/Anka.Test/` — xUnit tests, one file per feature/area (parser, transport, limits, RFC compliance, range requests, cache validation, multipart, streaming).
- `Benchmark/Anka.Benchmark/` — BenchmarkDotNet micro-benchmarks; the suite's job is to prove zero allocation on hot paths.
- `Test/LoadTest/` — `Anka.HttpConsole` (Native AOT target), `Kestrel.HttpConsole` (ASP.NET Core comparison baseline), `Anka.Wrk.LoadTest` (wrk-driven harness comparing the two).

## Conventions specific to this codebase

These are non-obvious and load-bearing for AOT/zero-allocation — violating them is a real regression, not a style nit.

1. **UTF-8 literals, not `Encoding.UTF8.GetBytes`, on any hot path.** `"Hello"u8.ToArray()` for bodies/content-types; AOT can't rely on JIT string interning.
2. **`HttpHeaderNames` entries are `=>`-bodied properties, never `static readonly` fields.** `ReadOnlySpan<byte>` is a ref struct and cannot be stored in a field — this simply won't compile if changed to a field.
3. **Keep `SequenceReader<byte>` (a ref struct) out of async state machines.** `HttpParser.TryParse(ref reader, ...)` must be called from a synchronous wrapper (see `Connection`'s `TryParseNext` pattern) when the call site is inside an `async` method — otherwise the compiler boxes the ref struct, which breaks AOT.
4. **Pre-build `HttpHeader[]` arrays once at startup** for any header set sent on every response (CORS, security headers) — building it per-request via the fluent `AddHeader` API allocates a `List` and is reserved for low-frequency paths.
5. **Keep-alive:** HTTP/1.1 defaults to keep-alive on (off only with `Connection: close`); HTTP/1.0 defaults off (on only with `Connection: keep-alive`). `HttpRequest.IsKeepAlive` is computed by `HttpParser.ComputeKeepAlive` during parsing — always forward `request.IsKeepAlive` into `WriteAsync`'s `keepAlive` parameter rather than hardcoding it.
6. **New internal types** go in `src/Anka/src/Internal/`, namespace `Anka`, marked `internal` — no need to make anything `public` for tests to reach it; `Anka.csproj` grants `InternalsVisibleTo` for `Anka.Test` and `Anka.Benchmark`.

## Testing patterns

Parser-level tests need no server:

```csharp
var bytes = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n");
var seq = new ReadOnlySequence<byte>(bytes);
var reader = new SequenceReader<byte>(seq);
var req = new HttpRequest();
Assert.True(HttpParser.TryParse(ref reader, req) == HttpParseResult.Success);
req.Return(); // always — resets the request so later tests start clean
```

Integration tests spin up a real server via the `TestServer` helper (see `TransportTests.cs` / `CustomResponseHeaderTests.cs`) and talk to it over a raw `TcpClient`:

```csharp
await using var server = await TestServer.StartAsync(
    static (req, res, ct) => res.WriteAsync(200, body, contentType, req.IsKeepAlive, ct));

using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, server.Port);
```

## RFC scope, quick reference

Full tables live in the README (`## RFC Compliance`). Headline points worth knowing before touching parsing/response code:
- Supported: HTTP/1.0 and 1.1 with dynamic response versioning, all origin/absolute/authority/asterisk request-target forms, chunked request *and response* bodies (response via `HttpResponseWriter.GetStream()`, trailers via `stream.AddTrailer(...)`), `Expect: 100-continue`, Range requests (`WritePartialAsync` → `206 Partial Content` + `Content-Range`), `If-None-Match`/`ETag` cache validation (auto `304`), `multipart/form-data` parsing (`MultipartParser`), configurable limits on body size / target size / header size returning 413/414/431, mandatory `Content-Length`/`Transfer-Encoding` on POST/PUT/PATCH (411 otherwise).
- Explicitly out of scope: HTTP/2, HTTP/3, TLS termination (put a reverse proxy in front), WebSocket upgrade, built-in Content-Encoding (gzip/br — decompress in user code), HTTP/0.9.
