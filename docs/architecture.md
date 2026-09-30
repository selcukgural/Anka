# Architecture

How Anka is built, for contributors and for anyone who wants to know what happens between `accept()` and the
handler. Library users do not need this document; start with the [README](../README.md).

AI agents and contributors should also read [`AGENTS.md`](../AGENTS.md) (conventions that are load-bearing for AOT
and zero allocation).

## Request lifecycle

One task per accepted TCP connection, one loop per keep-alive connection:

```
Server.StartAsync()
  → raises ThreadPool minimum threads (never lowers an existing setting)
  → binds an IPv4 or IPv6 socket ("::" is dual-stack), Listen(Backlog), raises ListeningStarted
  → N parallel AcceptLoopAsync()                 N = AcceptorCount, default max(ProcessorCount / 2, 2)
    → Connection.RunAsync(socket)                fire-and-forget per client (counted if MaxConcurrentConnections is set)
       loop:
         1. SocketReceiver.ReceiveAsync()        sync completion on loopback/LAN, ThreadPool continuation otherwise
         2. HttpParser.TryParseHeaders()         single pass: request line + headers into the pooled HttpRequest
         3. limits / framing checks              400 · 411 · 413 · 414 · 431 · 505 → response + close
         4. 100 Continue                         if Expect: 100-continue and a body is announced
         5. body read                            Content-Length copy, or chunked decode (+ trailers) into req.Body
         6. RequestHandler(req, res, ct)         user code
         7. response completion                  empty 200 if nothing written, terminating chunk if a chunked
                                                 response is still open, 500 + close if the handler threw
         8. keep-alive?                          next request (pipelined bytes are already in the buffer) or close

Shutdown (StartAsync's token cancelled)
  → accept loops stop, listener closes
  → connections not inside a handler: Socket.Shutdown (FIN) — the pending receive returns 0 and the loop exits
  → connections inside a handler: finish that request (response says Connection: close), then exit
  → after ShutdownTimeout: abort token cancelled — handler token cancelled, remaining sockets closed
  → StartAsync returns when the active-connection count reaches zero
```

A connection marks the handler phase with an `_inHandler` flag (`Volatile` read/write). It sets the flag and then
re-checks the stopping token before dispatching, and clears it before checking the token after the response, so
the shutdown callback and the request loop cannot both miss each other.

## Components

| Type | Visibility | Role |
|---|---|---|
| `Server` | public | Validates host/port, binds, runs the accept loops. |
| `Connection` | internal | Owns one socket: receive loop, parse loop, limits, body read, handler dispatch, keep-alive, cleanup. |
| `SocketReceiver` | internal | `SocketAsyncEventArgs` + `IValueTaskSource<int>`. Synchronous completions cost no allocation; async completions resume on the ThreadPool (`RunContinuationsAsynchronously = true`) so the kqueue/epoll thread is never blocked by request work. |
| `HttpParser` | internal | Single-pass parser: request line, all four request-target forms, header validation, `Content-Length` / `Transfer-Encoding` framing, `Host` validation, keep-alive. |
| `ChunkedBodyParser` | internal | `TryReadChunkSize`, `TryConsumeChunkData`, `TryConsumeTrailers` for chunked request bodies. |
| `HttpMethodParser` / `HttpVersionParser` | internal | Length-then-byte dispatch; no string comparisons. |
| `HttpRequestPool` | internal | 32-slot CAS pool (`Interlocked.Exchange` / `CompareExchange`). Not `ConcurrentQueue`, which allocates a new segment every 32 operations. |
| `HttpRequest` | public | Parsed request; reused for every request on a connection. |
| `HttpHeaders` | public | 64 inline entries (`[InlineArray(64)]`) of offsets into a shared buffer; names lowercased on ingest. |
| `HttpResponseWriter` | public | Builds the status line and headers into a connection-scoped pooled buffer, inlines small bodies (≤ 4 KB), writes straight to the socket. Tracks per-request response state (started, chunked, close). |
| `HttpResponseStream` | public | Connection-scoped `Stream` over the writer's chunked API. |
| `MultipartParser` | public | `ref struct` splitting a `multipart/form-data` body into parts; `TryGetBoundary` reads the boundary from `Content-Type`. |

`HttpParseResult`: `Success` · `Incomplete` · `Invalid` · `RequestTargetTooLong` · `HeaderFieldsTooLarge` ·
`HttpVersionNotSupported` · `ConflictingContentLength` · `MissingHostHeader` · `LengthRequired`.

## Memory model

```
Per connection (allocated once, reused for every request on it)
  ├── ArrayPool.Rent(64 KB)            receive buffer, sliding window — compacted only when the tail is full
  ├── HttpRequestPool.Rent()           HttpRequest + its header buffer (≤ 64 KB, kept when pooled)
  ├── HttpResponseWriter               ArrayPool header buffer (4.5 KB request → 8 KB rental) + HttpResponseStream
  └── SocketReceiver                   one SocketAsyncEventArgs

Per request
  ├── HttpRequest.ResetForReuse()      clears fields, keeps buffers
  ├── header buffer                    path, query and header names/values copied once; lookups are offsets
  └── body buffer                      grows as bytes arrive (never pre-sized from Content-Length)

Connection close
  ├── HttpRequestPool.Return()         returns the body buffer to ArrayPool, keeps the header buffer
  ├── ArrayPool.Return(receive buffer)
  ├── SocketReceiver.Dispose()
  └── HttpResponseWriter.Dispose()
```

Steady-state keep-alive traffic allocates nothing. Allocation that remains is per connection (pooled rentals) or
comes from user code (`req.Path` strings, `AddHeader` lists, JSON serialisation).

The request line plus headers must fit in the 64 KB receive buffer; a client that fills it without completing the
header block is disconnected. Header offsets are `ushort`, so request-target plus header storage is capped at
65,535 bytes regardless of the configured limits.

## Repository layout

```
Anka/
├── src/Anka/                     library (PublishAot, IsAotCompatible, InternalsVisibleTo: tests + benchmarks)
│   └── src/
│       ├── Core/                 public API: Server, ServerOptions, HttpRequest, HttpResponseWriter,
│       │                         HttpResponseStream, HttpHeaders, HttpHeader, HttpHeaderNames, ResponseContext,
│       │                         HttpResponseWriterExtensions, RequestMethod, HttpVersion, RequestHandler,
│       │                         MultipartParser
│       ├── Internal/             Connection, SocketReceiver, HttpParser, HttpParseResult, ChunkedBodyParser,
│       │                         HttpMethodParser, HttpVersionParser, HttpRequestPool,
│       │                         RequestTargetForm
│       ├── Extensions/           HttpRequestExtensions (internal helpers)
│       └── Exceptions/           AnkaArgumentException, AnkaOutOfRangeException
├── Test/Anka.Test/               xUnit: parser, transport, limits, RFC compliance, streaming, range, caching,
│                                 multipart, hardening regressions
├── Test/LoadTest/
│   ├── Anka.HttpConsole/         Native AOT target (also the CI smoke test)
│   ├── Kestrel.HttpConsole/      ASP.NET Core comparison target
│   └── Anka.Wrk.LoadTest/        wrk-driven startup + throughput harness
├── Benchmark/Anka.Benchmark/     BenchmarkDotNet; proves 0 B on hot paths
├── docs/                         architecture, performance, raw throughput results
├── scripts/run-linux-benchmark.sh
└── .github/workflows/            ci.yml, release.yml
```

## Tests

```bash
dotnet test Anka.slnx --nologo
dotnet test Test/Anka.Test --nologo --filter "FullyQualifiedName~TryParse_SimpleGet_ReturnsRequest"
```

Parser tests need no server; integration tests start a real `Server` on a free port and talk raw HTTP over
`TcpClient` (see `TransportTests.cs`). `HttpHardeningRegressionTests.cs` holds one test per fixed protocol bug.
