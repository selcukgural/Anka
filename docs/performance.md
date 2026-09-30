# Performance

> **Environment:** Apple M3 Max · 16 logical cores · macOS 26.3.1 · .NET 8.0.25 · Native AOT (osx-arm64)
> Microbenchmarks: `dotnet run --project Benchmark/Anka.Benchmark -c Release`
> End-to-end: `Test/LoadTest/Anka.Wrk.LoadTest` (wrk, 10 s per level, loopback)
> Raw results: one file per OS per run in this folder (e.g. [`throughput-results-macos-2026-04-16.md`](throughput-results-macos-2026-04-16.md)).

## Cold start

"Time to ready" = time between process start and the socket accepting connections.
"First response" = round-trip time of the very first HTTP request, measured from outside the process.

| | Anka (Native AOT) | Kestrel (JIT) | |
|---|---:|---:|---:|
| Time to listen | 411 ms ¹ | 203 ms | — |
| **Time to ready** | **2.3 ms** | 140 ms | 61× faster |
| First response | 20 ms | 26 ms | 1.3× faster |
| Startup allocation | **124.5 KB** | 2.5 MB | 20× less |
| RSS after first response | **15.1 MB** | 97.9 MB | 6.5× less |

¹ Kestrel binds the port faster because it reuses existing OS handles; Anka creates a fresh `Socket`. The JIT
warmup more than compensates: Kestrel needs ~140 ms after binding before it can serve, Anka 2.3 ms.

## End-to-end throughput — framework tests (wrk · c = 400, no database)

| Scenario | Anka (AOT) req/s | Kestrel (JIT) req/s |
|---|---:|---:|
| Plain Text GET | 133,000 | 141,700 |
| JSON API GET | 131,600 | 140,400 |
| GET w/ Multiple Headers | 133,300 | 140,900 |
| POST Echo (256 B body) | 127,100 | 131,200 |
| Large Response GET (~2 KB) | 129,800 | 135,700 |

Throughput is comparable. Kestrel's JIT-generated code edges ahead at high concurrency; Anka's advantage is
memory (~15 MB vs ~98 MB RSS) and startup cost, not raw requests per second.

## End-to-end throughput — TechEmpower-style DB tests (PostgreSQL · peak req/s)

| Scenario | Anka (AOT) | Kestrel (JIT) |
|---|---:|---:|
| Single DB Query | 23,500 | 24,500 |
| Multiple Queries (20) | 1,300 | 1,300 |
| Fortunes | 22,300 | 23,400 |
| DB Updates (20) | 622 | 629 |
| Cached Queries (100) | 107,200 | 145,600 |

DB-bound tests are limited by PostgreSQL connection-pool saturation, not by the HTTP layer.

## Microbenchmarks — zero allocations

BenchmarkDotNet v0.15.8 · .NET 8.0.25 · Arm64 RyuJIT. Every benchmark on a request/response hot path must report
**0 B** allocated; a non-zero value is treated as a regression.

### HTTP parser

Parses a complete raw HTTP/1.x byte buffer into `HttpRequest`.

| Benchmark | Mean | Allocated |
|---|---:|---:|
| SimpleGet | 92.9 ns | 0 B |
| GetWithManyHeaders (10 headers) | 420.0 ns | 0 B |
| PostWithSmallBody (256 B body) | 244.6 ns | 0 B |
| PostWithLargeBody (64 KB body) | 1,651 ns | 0 B |

### HTTP headers

`HttpHeaders` is an inline-array struct.

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Add_TenHeaders | 75.2 ns | 0 B |
| TryGetValue — byte span, first entry | 84.8 ns | 0 B |
| TryGetValue — byte span, last entry | 94.3 ns | 0 B |
| TryGetValue — byte span, missing | 92.7 ns | 0 B |
| TryGetValue — string, case-insensitive | 94.6 ns | 0 B |

### HTTP method and version parsers

| Method | Mean | | Version | Mean |
|---|---:|---|---|---:|
| GET | 0.71 ns | | HTTP/1.1 | 0.09 ns |
| POST | 0.99 ns | | HTTP/1.0 | 0.21 ns |
| PUT | 0.71 ns | | | |
| HEAD | 0.98 ns | | | |
| PATCH | 1.32 ns | | | |
| DELETE | 1.57 ns | | | |
| OPTIONS | 1.83 ns | | | |
| CONNECT | 1.90 ns | | | |

The chunked-body parser and the chunked response stream benchmarks (`ChunkedBodyParserBenchmarks`,
`HttpResponseStreamBenchmarks`) also report 0 B.

## Running the benchmarks

```bash
# Microbenchmarks (Release, expect 0 B allocated)
dotnet run --project Benchmark/Anka.Benchmark -c Release

# Startup + throughput comparison against Kestrel (needs wrk; DB scenarios need PostgreSQL)
dotnet run --project Test/LoadTest/Anka.Wrk.LoadTest --configuration Release

# Linux, in Docker/Podman — PostgreSQL is started and torn down automatically
./scripts/run-linux-benchmark.sh              # linux/amd64
./scripts/run-linux-benchmark.sh linux/arm64  # native on Apple Silicon
```

The Linux script writes `docs/throughput-results-linux-{date}.md`.
