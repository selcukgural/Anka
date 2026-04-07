using Anka;
using System.Text;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8080;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Pre-allocated response bodies and content types (avoids per-request allocations).
var plainBody    = "Hello from Anka!"u8.ToArray();
var jsonBody     = """{"status":"ok","server":"Anka","version":"0.0.1","message":"Hello from Anka!"}"""u8.ToArray();
var largeBody    = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Anka is a minimal, zero-allocation HTTP/1.x server for .NET. ", 36))); // ~2 KB
var okBody       = "OK"u8.ToArray();

ReadOnlyMemory<byte> textPlainCt = "text/plain; charset=utf-8"u8.ToArray();
ReadOnlyMemory<byte> appJsonCt   = "application/json; charset=utf-8"u8.ToArray();
ReadOnlyMemory<byte> textPlainNoCt = "text/plain"u8.ToArray();

var server = new Server(
    handler: async (req, res, ct) =>
    {
        if (req.PathEquals("/plain"u8) || req.PathEquals("/"u8))
        {
            await res.WriteAsync(200, plainBody, textPlainCt, cancellationToken: ct);
        }
        else if (req.PathEquals("/json"u8))
        {
            await res.WriteAsync(200, jsonBody, appJsonCt, cancellationToken: ct);
        }
        else if (req.PathEquals("/headers"u8))
        {
            await res.WriteAsync(200, plainBody, textPlainCt, cancellationToken: ct);
        }
        else if (req.PathEquals("/echo"u8))
        {
            // Body is already fully buffered by the time the handler runs.
            await res.WriteAsync(200, req.Body, appJsonCt, cancellationToken: ct);
        }
        else if (req.PathEquals("/large"u8))
        {
            await res.WriteAsync(200, largeBody, textPlainCt, cancellationToken: ct);
        }
        else if (req.PathEquals("/health"u8))
        {
            await res.WriteAsync(200, okBody, textPlainNoCt, cancellationToken: ct);
        }
        else
        {
            await res.WriteAsync(200, plainBody, textPlainCt, cancellationToken: ct);
        }
    },
    port: port);

await server.StartAsync(cts.Token);