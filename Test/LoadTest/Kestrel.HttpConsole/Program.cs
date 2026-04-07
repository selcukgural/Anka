using System.Text;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8081;

// Pre-allocate all response bodies once — same approach as Anka.HttpConsole.
var plainBody = "Hello from Kestrel!"u8.ToArray();
var jsonBody  = """{"status":"ok","server":"Kestrel","version":"8.0","message":"Hello from Kestrel!"}"""u8.ToArray();
var largeBody = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Anka is a minimal, zero-allocation HTTP/1.x server for .NET. ", 36)));
var okBody    = "OK"u8.ToArray();

// CreateSlimBuilder: Kestrel + DI + config, no routing, no authentication, no logging pipelines.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args });

builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

// Suppress all console output so Kestrel doesn't compete with the process for I/O bandwidth.
builder.Logging.ClearProviders();

var app = builder.Build();

// Raw terminal handler — no routing middleware overhead.
app.Run(async context =>
{
    var path     = context.Request.Path.Value;
    var response = context.Response;

    switch (path)
    {
        case "/plain" or "/":
            response.ContentType   = "text/plain; charset=utf-8";
            response.ContentLength = plainBody.Length;
            await response.BodyWriter.WriteAsync(plainBody);
            break;
        case "/json":
            response.ContentType   = "application/json; charset=utf-8";
            response.ContentLength = jsonBody.Length;
            await response.BodyWriter.WriteAsync(jsonBody);
            break;
        case "/headers":
            // Same handler as /plain — exercises header parsing on the client side.
            response.ContentType   = "text/plain; charset=utf-8";
            response.ContentLength = plainBody.Length;
            await response.BodyWriter.WriteAsync(plainBody);
            break;
        case "/echo":
        {
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms);
            var body = ms.ToArray();

            response.ContentType   = "application/json; charset=utf-8";
            response.ContentLength = body.Length;
            await response.BodyWriter.WriteAsync(body);
            break;
        }
        case "/large":
            response.ContentType   = "text/plain; charset=utf-8";
            response.ContentLength = largeBody.Length;
            await response.BodyWriter.WriteAsync(largeBody);
            break;
        case "/health":
            response.ContentType   = "text/plain";
            response.ContentLength = okBody.Length;
            await response.BodyWriter.WriteAsync(okBody);
            break;
        default:
            response.ContentType   = "text/plain; charset=utf-8";
            response.ContentLength = plainBody.Length;
            await response.BodyWriter.WriteAsync(plainBody);
            break;
    }
});

await app.RunAsync();
