# Anka

> **Versiyon: v0.0.1**

Anka, .NET 8+ üzerinde raw socket ve `System.IO.Pipelines` kullanılarak sıfırdan yazılmış, **sıfır-allocation** tasarımlı minimal bir HTTP/1.x sunucu kütüphanesidir. AOT (Ahead-of-Time) derlemeyle tam uyumludur.

---

## İçindekiler

1. [Özellikler ve Kısıtlamalar](#özellikler-ve-kısıtlamalar)
2. [Hızlı Başlangıç](#hızlı-başlangıç)
3. [Mimari Genel Görünüm](#mimari-genel-görünüm)
4. [Uygulama Yaşam Döngüsü](#uygulama-yaşam-döngüsü) — başlangıçtan bitişe adım adım
5. [Sınıf Referansı — Public API](#sınıf-referansı--public-api)
6. [Sınıf Referansı — Internal](#sınıf-referansı--internal)
7. [Bellek Modeli](#bellek-modeli)
8. [Performans Profili](#performans-profili)
9. [Proje Yapısı](#proje-yapısı)

---

## Özellikler ve Kısıtlamalar

### ✅ Desteklenen

| Alan                  | Detay                                                                                                                   |
|-----------------------|-------------------------------------------------------------------------------------------------------------------------|
| **HTTP Versiyonları** | HTTP/1.0, HTTP/1.1                                                                                                      |
| **HTTP Metodları**    | GET, POST, PUT, DELETE, HEAD, OPTIONS, PATCH, TRACE, CONNECT                                                            |
| **Keep-Alive**        | HTTP/1.1 default açık; `Connection: close` ile kapatılır. HTTP/1.0 default kapalı; `Connection: keep-alive` ile açılır. |
| **Request Body**      | `Content-Length` ile belirtilen body tam olarak okunur                                                                  |
| **Request Headers**   | En fazla 64 header; isimler lowercase normalize edilir                                                                  |
| **Response**          | Status code, body, Content-Type, Content-Length, Connection header                                                      |
| **Response Body**     | ≤ 4096 byte: header buffer'ına inline eklenir (tek `SendAsync`). > 4096 byte: ayrı `SendAsync`                          |
| **Concurrency**       | Her bağlantı bağımsız bir `Task` olarak çalışır; accept loop hiçbir zaman bir bağlantıyı beklemez                       |
| **Backpressure**      | `PipeOptions.pauseWriterThreshold = 64 KB`, `resumeWriterThreshold = 32 KB`                                             |
| **AOT Uyumluluk**     | `PublishAot=true`, `IsAotCompatible=true` — reflection yok, generics limitation yok                                     |
| **Bellek**            | `ArrayPool<byte>` + CAS single-slot `HttpRequestPool` → istek başına **0 B** heap allocation                            |

### ❌ Desteklenmeyen

| Alan                          | Detay                                                                                  |
|-------------------------------|----------------------------------------------------------------------------------------|
| **HTTP/2 & HTTP/3**           | Yalnızca HTTP/1.x                                                                      |
| **TLS / HTTPS**               | SSL/TLS wrapper yok                                                                    |
| **Chunked Transfer Encoding** | `Transfer-Encoding: chunked` parse edilmez; body yok sayılır                           |
| **Request URL Decoding**      | `%xx` percent-encoding ve `+`→space dönüşümü yapılmaz                                  |
| **Routing**                   | Yerleşik route matcher / middleware sistemi yok                                        |
| **WebSocket**                 | Upgrade akışı yok                                                                      |
| **Trailer Headers**           | HTTP/1.1 trailer desteği yok                                                           |
| **100-Continue**              | `Expect: 100-continue` yanıtlanmaz                                                     |
| **IPv6**                      | Yalnızca IPv4 (`AddressFamily.InterNetwork`)                                           |
| **Graceful Shutdown**         | `CancellationToken` iptal edildiğinde aktif bağlantılar beklenilmez; accept loop durur |

---

## Hızlı Başlangıç

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

`Ctrl+C` ile graceful shutdown.

---

## Mimari Genel Görünüm

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
                        │          │  (kullanıcı kodu)  │          │
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

**Veri Akışı:**

```
TCP bytes → PipeWriter → [Pipe buffer] → PipeReader
         → HttpParser.ScanForComplete()  (Phase 1 — sıfır allocation)
         → HttpParser.ParseCore()        (Phase 2 — 1-2 ArrayPool.Rent)
         → HttpRequest                   (pool'dan verilir)
         → handler(request, response)    (kullanıcı kodu)
         → request.Return()              (buffers → ArrayPool, instance → pool)
```

---

## Uygulama Yaşam Döngüsü

### Adım 1 — Server Oluşturma

```csharp
new Server(handler, port: 8080)
```

`Server` constructor'ı:
1. Port aralığı kontrolü: `port < 1 || port > 65535` → `AnkaPortOutOfRageException` fırlatır
2. IP parse kontrolü: `IPAddress.TryParse(host)` başarısız → `AnkaArgumentException` fırlatır
3. `_endPoint = new IPEndPoint(ip, port)` — henüz soket yok

---

### Adım 2 — Server.StartAsync()

```csharp
await server.StartAsync(cancellationToken);
```

1. `Socket(InterNetwork, Stream, Tcp)` oluşturulur
2. `socket.NoDelay = true` (Nagle algoritması devre dışı)
3. `socket.ReuseAddress = true`
4. `socket.Bind(_endPoint)` + `socket.Listen(backlog: 512)`
5. `Console.WriteLine($"Listening on {_endPoint}")` yazdırılır
6. **Accept döngüsü** başlar:

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    var client = await socket.AcceptAsync(cancellationToken);
    _ = Connection.RunAsync(client, _handler, cancellationToken); // fire & forget
}
```

Her kabul edilen bağlantı için `Connection.RunAsync` fire-and-forget olarak çağrılır.  
Accept döngüsü **hiçbir zaman** bir bağlantının bitmesini beklemez.

---

### Adım 3 — Connection.RunAsync()

```csharp
Connection.RunAsync(socket, handler, cancellationToken)
```

1. `socket.NoDelay = true` (bağlantı bazında da set edilir)
2. `new Connection(socket, handler, cancellationToken)` oluşturulur
3. `ExecuteAsync()` çağrılır:

```csharp
await Task.WhenAll(FillPipeAsync(), ReadPipeAsync());
// finally: socket.Close() + socket.Dispose()
```

İki async görev **eş zamanlı** çalışır:

---

### Adım 4 — FillPipeAsync() — Socket → Pipe

```
Socket.ReceiveAsync → PipeWriter.GetMemory(4096) → Advance(bytesRead) → FlushAsync
```

- Her iterasyonda en az 4096 byte'lık bellek bloğu istenir
- `bytesRead == 0` → bağlantı kapandı, döngü biter
- `flush.IsCompleted` → reader pipe'ı tüketmeyi bıraktı (yavaş istemci backpressure)
- `OperationCanceledException` / `SocketException` → sessizce çıkar
- `finally`: `PipeWriter.CompleteAsync()` → reader tarafını bilgilendirir

---

### Adım 5 — ReadPipeAsync() — Pipe → Parse → Handle

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

Her `ReadAsync` çağrısı mevcut Pipe bufferını döndürür.  
`AdvanceTo(consumed, examined)`:
- `consumed`: bu kadar byte gerçekten tükendi
- `examined`: bu kadar byte bakıldı (ama yetersizse geri verilir)

Keep-alive durumunda döngü yeni isteği bekler.  
`Connection: close` veya HTTP/1.0 (keep-alive header'sız) → `break` → bağlantı kapanır.

---

### Adım 6 — HttpParser.TryParse() — İki Aşamalı Parse

#### Phase 1: ScanForComplete (sıfır allocation)

```
scanner (reader kopyası) üzerinden ilerle:
  1. Request line'ı atla (\r\n)
  2. Header satırlarını tara:
     - Satır "c" ile başlıyor ve >15 byte? → TryExtractContentLength()
     - Boş satır (\r\n\r\n) → header sonu
  3. Body varsa: reader.Remaining >= contentLength?
```

Bu aşama **kopyalama yapmaz**, sadece pointer ilerletir. Veri yetersizse `false` döner.

#### Phase 2: ParseCore (1 veya 2 ArrayPool.Rent)

```
buf = ArrayPool.Rent(headerWireBytes veya min 256)
req = HttpRequestPool.Rent()  ← pool'dan al
req.Buffer = buf

  ParseRequestLine():
    METHOD SP path[?query] SP HTTP/x.y\r\n
    → HttpMethodParser.Parse(methodSpan)
    → HttpVersionParser.Parse(versionSpan)
    → path kopyalanır buf'a; req.SetPath(offset, length)
    → query kopyalanır buf'a; req.SetQuery(offset, length)

  Headers döngüsü:
    Her \r\n ile biten satır:
      "Name: Value" → headers.Add(name, value)
    Boş satır → sona

  Body (contentLength > 0):
    bodyBuf = ArrayPool.Rent(contentLength)
    reader.TryCopyTo(bodyBuf)
    req.BodyBuffer = bodyBuf
    req.Body = bodyBuf.AsMemory(0, contentLength)

  ComputeKeepAlive():
    Connection header'ına bak → IsKeepAlive belirle

  request = req
  success = true
```

**Başarısızlık durumunda** (finally bloğu):
- `req != null` → `HttpRequestPool.Return(req)` → `Reset()` → iki buffer ArrayPool'a iade
- `req == null` (Rent() atmadan önce): `buf` + `bodyBuf` manuel iade edilir

---

### Adım 7 — Handler Çağrısı

```csharp
await _handler(request!, responseWriter, _cancellationToken);
```

Kullanıcı kodu bu noktada:
- `request.Method`, `request.Path`, `request.QueryString`, `request.Headers`, `request.Body` okur
- `response.WriteAsync(statusCode, body, contentType, keepAlive)` çağırır

---

### Adım 8 — HttpResponseWriter.WriteAsync()

```
buf = ArrayPool.Rent(512 + smallBodyThreshold)

  WriteStatusLine()   → "HTTP/1.1 200 OK\r\n"
  WriteLiteral()      → "Server: Anka\r\n"
  WriteContentLength()→ "Content-Length: N\r\n"
  WriteLiteral()      → "Connection: keep-alive\r\n" veya "Connection: close\r\n"
  (contentType)       → "Content-Type: ...\r\n"
  span[pos++] = '\r'; span[pos++] = '\n'  ← header sonu

  body <= 4096: body inline kopyalanır → tek SendAsync
  body >  4096: header gönderilir, sonra body ayrıca SendAsync
```

`finally`: `ArrayPool.Return(buf)`

---

### Adım 9 — request.Return()

```csharp
request!.Return(); // Connection.ReadPipeAsync içindeki finally bloğu
```

`Return()` → `HttpRequestPool.Return(this)`:
1. `req.Reset()`:
   - `Buffer` → `ArrayPool.Return(Buffer)` → `Buffer = null`
   - `BodyBuffer` → `ArrayPool.Return(BodyBuffer)` → `BodyBuffer = null`
   - Tüm alanlar sıfırlanır (Method, Version, Path, Query, Headers, Body, IsKeepAlive)
2. `Interlocked.CompareExchange(ref _slot, req, null)`:
   - Slot boşsa: instance pool'a konur (bir sonraki istek için hazır)
   - Slot doluysa: instance GC'ye bırakılır

---

### Adım 10 — Kapatma

`CancellationToken.Cancel()` çağrıldığında:

1. `socket.AcceptAsync` → `OperationCanceledException` fırlatır
2. `Server.StartAsync` bunu yakalar → `Console.WriteLine("Server shutting down.")`
3. `using var socket` → dispose → server soketi kapanır
4. Aktif `Connection` görevleri: `FillPipeAsync`'deki `ReceiveAsync` ve `ReadPipeAsync`'deki `ReadAsync` aynı token'ı kullanır → `OperationCanceledException` → sessizce çıkar
5. Her `Connection.ExecuteAsync` finally bloğu → `socket.Close()` + `socket.Dispose()`

---

## Sınıf Referansı — Public API

### `Server`

```
Namespace: Anka
Erişim:    public sealed
```

**Constructor**

```csharp
Server(RequestHandler handler, int port, string host = "127.0.0.1")
```

| Parametre | Açıklama                                         |
|-----------|--------------------------------------------------|
| `handler` | Her HTTP isteği için çağrılacak delegate         |
| `port`    | TCP port numarası (1–65535)                      |
| `host`    | Dinlenecek IPv4 adresi (varsayılan: `127.0.0.1`) |

**Fırlatılan İstisnalar:**
- `AnkaPortOutOfRageException` — port 1–65535 aralığı dışında
- `AnkaArgumentException` — geçersiz IP adresi

**Method**

```csharp
Task StartAsync(CancellationToken cancellationToken = default)
```

Sunucuyu başlatır. Token iptal edilene kadar döner. Dönmesi = sunucu durdu.

---

### `HttpRequest`

```
Namespace: Anka
Erişim:    public sealed
```

Ayrıştırılmış bir HTTP isteğini temsil eder. Handler tamamlandıktan sonra **kullanılmamalıdır** — `Return()` çağrısıyla nesne havuza iade edilir.

| Üye           | Tür                    | Açıklama                                              |
|---------------|------------------------|-------------------------------------------------------|
| `Method`      | `HttpMethod`           | GET, POST, ...                                        |
| `Version`     | `HttpVersion`          | Http10, Http11                                        |
| `Path`        | `string`               | Lazy-materialized path string (`/api/users`)          |
| `PathBytes`   | `ReadOnlySpan<byte>`   | Zero-copy ham path baytları                           |
| `QueryString` | `string?`              | Lazy-materialized query (`foo=bar`). `?` yoksa `null` |
| `QueryBytes`  | `ReadOnlySpan<byte>`   | Zero-copy ham query baytları                          |
| `Headers`     | `HttpHeaders`          | Header koleksiyonu (struct, inline)                   |
| `Body`        | `ReadOnlyMemory<byte>` | Request body. Content-Length yoksa boş.               |
| `IsKeepAlive` | `bool`                 | Bağlantı kalıcı mı?                                   |

**Not:** `Path` ve `QueryString` ilk okunduğunda `Encoding.ASCII.GetString()` çağrılır ve önbelleğe alınır. `PathBytes` ve `QueryBytes` hiçbir zaman allocation yapmaz.

---

### `HttpResponseWriter`

```
Namespace: Anka
Erişim:    public sealed
```

HTTP/1.1 yanıtı yazar. `ArrayPool` + `Utf8Formatter` ile sıfır string allocation.

**Method**

```csharp
ValueTask WriteAsync(
    int statusCode,
    ReadOnlyMemory<byte> body           = default,
    string? contentType                 = null,
    bool keepAlive                      = true,
    CancellationToken cancellationToken = default)
```

| Parametre     | Açıklama                                      |
|---------------|-----------------------------------------------|
| `statusCode`  | HTTP status code (200, 404, 500 vb.)          |
| `body`        | Yanıt gövdesi (opsiyonel)                     |
| `contentType` | Content-Type header değeri (opsiyonel)        |
| `keepAlive`   | `Connection: keep-alive` mi yoksa `close` mi? |

**Desteklenen Status Code Reason Phrase'leri:**
200 OK · 201 Created · 204 No Content · 301 Moved Permanently · 302 Found · 304 Not Modified · 400 Bad Request · 401 Unauthorized · 403 Forbidden · 404 Not Found · 405 Method Not Allowed · 500 Internal Server Error · 503 Service Unavailable · diğerleri → "Unknown"

---

### `HttpHeaders`

```
Namespace: Anka
Erişim:    public struct
```

Heap allocation'sız header koleksiyonu. `[InlineArray(64)]` ile 64 header entry struct içinde gömülüdür.

| Üye                                                       | Açıklama                                                       |
|-----------------------------------------------------------|----------------------------------------------------------------|
| `Count`                                                   | Kaç header eklendiği                                           |
| `TryGetValue(ReadOnlySpan<byte>, out ReadOnlySpan<byte>)` | Zero-alloc lookup. `lowercaseName` **zaten lowercase** olmalı. |
| `TryGetValue(string, out ReadOnlySpan<byte>)`             | `stackalloc` ile lowercase dönüşüm. 128 karakter sınırı var.   |

**Önemli:** Header isimleri `Add()` sırasında **lowercase normalize edilir**. Lookup her zaman `SequenceEqual` ile yapılır.  
`HttpHeaderNames` sabitleri zaten lowercase olduğu için doğrudan kullanılabilir:

```csharp
if (request.Headers.TryGetValue(HttpHeaderNames.ContentType, out var ct))
{
    // ct = ReadOnlySpan<byte> — sıfır allocation
}
```

---

### `HttpHeaderNames`

```
Namespace: Anka
Erişim:    public static
```

Yaygın kullanılan header isimlerini lowercase `ReadOnlySpan<byte>` olarak sağlar.

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

Sunucuya geçilen kullanıcı callback'i. Her HTTP isteği için çağrılır.

---

### `AnkaArgumentException`

`ArgumentException`'dan türer. Geçersiz argüman verildiğinde fırlatılır (örn. geçersiz IP).

### `AnkaPortOutOfRageException`

`ArgumentOutOfRangeException`'dan türer. Port 1–65535 dışındayken fırlatılır.

---

## Sınıf Referansı — Internal

### `Connection` (internal sealed)

Her TCP bağlantısının yaşam döngüsünü yönetir.

| Üye                                    | Açıklama                                                                                                          |
|----------------------------------------|-------------------------------------------------------------------------------------------------------------------|
| `static RunAsync(socket, handler, ct)` | Tek public entry point. `socket.NoDelay=true` set eder, yeni `Connection` oluşturur, `ExecuteAsync()` çalıştırır. |
| `ExecuteAsync()`                       | `Task.WhenAll(FillPipeAsync, ReadPipeAsync)` çalıştırır. `finally`: `socket.Close()` + `Dispose()`                |
| `FillPipeAsync()`                      | Socket → PipeWriter. Sürekli `ReceiveAsync` → `Advance` → `FlushAsync`.                                           |
| `ReadPipeAsync()`                      | PipeReader → `HttpParser.TryParse` → `handler` → `request.Return()`. Keep-alive döngüsü burada.                   |

**PipeOptions:**
```
pauseWriterThreshold:  64 KB  (yavaş okuyucu → yazmayı durdur)
resumeWriterThreshold: 32 KB  (tampon yarıya inince → devam et)
useSynchronizationContext: false  (ThreadPool'da çalış)
```

---

### `HttpParser` (internal static)

HTTP/1.x isteğini ayrıştırır.

| Üye                                                    | Açıklama                                                    |
|--------------------------------------------------------|-------------------------------------------------------------|
| `TryParse(ref SequenceReader<byte>, out HttpRequest?)` | İki aşamalı parse. Veri yetersizse `false` döner.           |
| `ScanForComplete(ref reader, out contentLength)`       | Phase 1: sıfır allocation tarama                            |
| `TryExtractContentLength(line, ref contentLength)`     | `content-length:` header değerini çıkarır                   |
| `ParseRequestLine(seq, buf, ref writePos, req)`        | Method + path + query + version ayrıştırır                  |
| `ParseHeaderLine(seq, ref headers)`                    | Tek header satırını ayrıştırır                              |
| `AddHeaderFromSpan(line, ref headers)`                 | `Name: Value` parçalar, `headers.Add()` çağırır             |
| `ComputeKeepAlive(version, ref headers)`               | HTTP version + Connection header'dan `IsKeepAlive` hesaplar |

---

### `HttpMethodParser` (internal static)

| Üye                         | Açıklama                                                    |
|-----------------------------|-------------------------------------------------------------|
| `Parse(ReadOnlySpan<byte>)` | Byte span → `HttpMethod` enum. Bilinmeyen → `Unknown`       |
| `ToBytes(this HttpMethod)`  | `HttpMethod` enum → `ReadOnlySpan<byte>` (extension method) |

---

### `HttpVersionParser` (internal static)

| Üye                         | Açıklama                                                            |
|-----------------------------|---------------------------------------------------------------------|
| `Parse(ReadOnlySpan<byte>)` | `"HTTP/1.1"` → `Http11`, `"HTTP/1.0"` → `Http10`, diğer → `Unknown` |

---

### `HttpRequestPool` (internal static)

CAS tabanlı single-slot object pool. Lock-free, AOT-safe.

| Üye           | Açıklama                                                                                                          |
|---------------|-------------------------------------------------------------------------------------------------------------------|
| `Rent()`      | `Interlocked.Exchange(ref _slot, null)` — slot doluysa döndürür, boşsa `new HttpRequest()`                        |
| `Return(req)` | `req.Reset()` sonra `Interlocked.CompareExchange(ref _slot, req, null)` — slot doluysa instance GC'ye terk edilir |

**Neden ConcurrentQueue değil?**  
`ConcurrentQueue<T>` her 32 enqueue/dequeue sonrasında yeni iç segment (~608 byte) allocate eder. Bu mikrobenchmark'ta 608 B görünmesine neden oluyordu. CAS single-slot ile **0 B** sağlandı.

---

## Bellek Modeli

```
Her HTTP isteği için bellek hareketi:

  HttpParser.TryParse()
  ├── ArrayPool.Rent(headerWireBytes)     ← buf: path + query + header isimleri/değerleri
  ├── HttpRequestPool.Rent()              ← HttpRequest instance (pool'dan veya new)
  └── ArrayPool.Rent(contentLength)       ← bodyBuf (sadece body varsa)

  handler() çalışır...

  request.Return()
  ├── ArrayPool.Return(buf)
  ├── ArrayPool.Return(bodyBuf)           ← sadece body varsa
  └── HttpRequestPool._slot = req         ← CAS ile tek slota yerleştir

  HttpResponseWriter.WriteAsync()
  ├── ArrayPool.Rent(512 + bodyThreshold) ← header buffer
  └── ArrayPool.Return(buf)               ← finally
```

**Sonuç:** Tekrar eden istekler için **0 heap allocation** (`ArrayPool` ve object pool sayesinde).

---

## Performans Profili

BenchmarkDotNet (Release / net10.0) ölçümleri:

| Senaryo                          | Mean    | Gen0 | Allocated |
|----------------------------------|---------|------|-----------|
| SimpleGet (minimal GET)          | ~129 ns | 0    | **0 B**   |
| GetWithManyHeaders (10 header)   | ~380 ns | 0    | **0 B**   |
| PostWithSmallBody (128 B body)   | ~160 ns | 0    | **0 B**   |
| PostWithLargeBody (64 KB body)   | ~4.1 µs | 0    | **0 B**   |
| Header lookup — hit (byte span)  | ~8 ns   | 0    | 0 B       |
| Header lookup — miss (byte span) | ~5 ns   | 0    | 0 B       |
| HttpMethod.Parse (GET)           | ~0.3 ns | 0    | 0 B       |

**Diğer sunucularla karşılaştırma (parsing katmanı):**

| Sunucu / Katman        | Alloc / istek | Notlar                                       |
|------------------------|---------------|----------------------------------------------|
| Anka                   | **0 B**       | ArrayPool + CAS pool                         |
| ASP.NET Core (Kestrel) | ~300–600 B    | Framework overhead (routing, middleware, DI) |
| HttpListener           | ~1–2 KB       | Her istek için managed nesneler              |

*Not: Bu karşılaştırma yalnızca parsing katmanını ölçer. Kestrel'in routing ve middleware pipelineı farklı trade-off'lar sunar.*

---

## Proje Yapısı

```
Anka/
├── Anka.slnx
│
├── Anka/                          ← Kütüphane
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
├── Anka.Console/                  ← Örnek uygulama
│   └── Program.cs                   (Hello World server on :8080)
│
├── Anka.Test/                     ← xUnit testleri (108 test)
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

## Katkı ve Geliştirme

### Test Koşturma

```bash
dotnet test Anka.Test
```

### Benchmark Koşturma

```bash
cd Anka.Benchmark
dotnet run -c Release
```

### Yük Testi

Sunucuyu başlat:
```bash
cd Anka.Console
dotnet run -c Release
```

Harici araçlarla yük testi:
```bash
# wrk
wrk -t4 -c100 -d30s http://localhost:8080/

# hey
hey -n 100000 -c 100 http://localhost:8080/
```
