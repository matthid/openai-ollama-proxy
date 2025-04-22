// Program.cs (.NET 7+ minimal API)
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
var app     = builder.Build();

// ————— CONFIG —————
var remoteBase = Environment.GetEnvironmentVariable("OPENWEBUI_ENDPOINT");
var apiKey     = Environment.GetEnvironmentVariable("OPENWEBUI_API_KEY");
// ——————————————————

var httpClient = new HttpClient { BaseAddress = new Uri(remoteBase) };
httpClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", apiKey);

// Generic proxy helper
static async Task Proxy(HttpContext ctx, HttpClient client, string path)
{
    // build the upstream URI
    var upstream = path + ctx.Request.QueryString;

    // start building our outgoing HttpRequestMessage
    var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstream);

    // if there's a request body, attach it as stream content
    if (ctx.Request.ContentLength.GetValueOrDefault() > 0
        || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        // wrap the raw request body in StreamContent
        var sc = new StreamContent(ctx.Request.Body);

        // copy the Content-Type header if present
        if (!string.IsNullOrEmpty(ctx.Request.ContentType))
            sc.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ctx.Request.ContentType);

        // **remove** any Content-Length header so HttpClient doesn't try to enforce it
        sc.Headers.Remove("Content-Length");

        req.Content = sc;

        // ask HttpClient to use chunked transfer encoding
        req.Headers.TransferEncodingChunked = true;
    }

    // copy *other* headers (User-Agent, Authorization, custom, etc.), but skip Host
    foreach (var h in ctx.Request.Headers)
    {
        if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
        req.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
    }

    // send upstream and ask to stream the response headers & body as they come
    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

    // copy status code
    ctx.Response.StatusCode = (int)resp.StatusCode;

    // copy response headers
    foreach (var h in resp.Headers)
        ctx.Response.Headers[h.Key] = h.Value.ToArray();
    foreach (var h in resp.Content.Headers)
        ctx.Response.Headers[h.Key] = h.Value.ToArray();

    // remove chunked encodings that Kestrel will set itself
    ctx.Response.Headers.Remove("transfer-encoding");

    // finally, stream the response body back to the caller
    await resp.Content.CopyToAsync(ctx.Response.Body);
}

// 1) /api/tags  → remote GET /api/models  → Ollama shape

app.MapGet("/api/tags", async ctx =>
{
    // 1) fetch remote model list
    var upstream = await httpClient.GetAsync("/api/models");
    ctx.Response.StatusCode = (int)upstream.StatusCode;

    if (!upstream.IsSuccessStatusCode)
    {
        // proxy errors straight through
        await upstream.Content.CopyToAsync(ctx.Response.Body);
        return;
    }

    // 2) parse JSON
    using var stream = await upstream.Content.ReadAsStreamAsync();
    using var doc    = await JsonDocument.ParseAsync(stream);
    var root         = doc.RootElement;

    // 3) unwrap "data": [...] if present
    var arr = root;
    if (root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("data", out var maybeData)
        && maybeData.ValueKind == JsonValueKind.Array)
    {
        arr = maybeData;
    }

    // 4) extract the nested "ollama" object from each entry
    var list = new List<JsonElement>();
    foreach (var item in arr.EnumerateArray())
    {
        if (!item.TryGetProperty("ollama", out var ollamaVal))
            continue;

        if (ollamaVal.ValueKind == JsonValueKind.String)
        {
            // Sometimes "ollama" is serialized as a string containing JSON
            var inner = JsonDocument.Parse(ollamaVal.GetString()!).RootElement;
            list.Add(inner);
        }
        else if (ollamaVal.ValueKind == JsonValueKind.Object)
        {
            list.Add(ollamaVal);
        }
    }

    // 5) write back as JSON array
    ctx.Response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(ctx.Response.Body, new { models = list });
});

// 2) /api/generate  → remote /api/generate
app.MapPost("/api/generate", ctx => Proxy(ctx, httpClient, "/api/generate"));

// 3) /api/chat      → remote /api/chat/completions
// helper to read lines asynchronously
static async IAsyncEnumerable<string> ReadLinesAsync(Stream s)
{
    var reader = new StreamReader(s);
    while (!reader.EndOfStream)
    {
        var line = await reader.ReadLineAsync();
        if (line is not null) yield return line;
    }
}

// Replace your old /api/chat proxy with this:
app.MapPost("/api/chat", async ctx =>
{
    // 1) Buffer the incoming JSON so we can inject "stream": true
    using var msIn = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(msIn);
    var inJson = Encoding.UTF8.GetString(msIn.ToArray());

    using var inDoc = JsonDocument.Parse(inJson);
    var root       = inDoc.RootElement;

    // 2) Re-serialize, copying all props and forcing stream=true
    using var msBody = new MemoryStream();
    await using (var w = new Utf8JsonWriter(msBody))
    {
        w.WriteStartObject();
        foreach (var p in root.EnumerateObject())
            p.WriteTo(w);
        w.WriteBoolean("stream", true);
        w.WriteEndObject();
    }
    var newBody = msBody.ToArray();

    // 3) Send to remote with stream=true
    var req = new HttpRequestMessage(HttpMethod.Post,
                "/api/chat/completions")
    {
        Content = new ByteArrayContent(newBody)
    };
    req.Content.Headers.ContentType =
        new MediaTypeHeaderValue("application/json");

    using var upResp = await httpClient.SendAsync(
        req,
        HttpCompletionOption.ResponseHeadersRead
    );

    // 4) Prepare our response
    ctx.Response.StatusCode  = (int)upResp.StatusCode;
    ctx.Response.ContentType = "application/x-ndjson";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    // helper: read lines from SSE stream
    async IAsyncEnumerable<string> ReadLines(Stream s)
    {
        using var rdr = new StreamReader(s);
        while (!rdr.EndOfStream)
        {
            var line = await rdr.ReadLineAsync();
            if (line is not null) yield return line;
        }
    }

    // 5) Transform and forward each SSE chunk
    await foreach (var line in ReadLines(await upResp.Content.ReadAsStreamAsync()))
    {
        if (!line.StartsWith("data:")) continue;
        var payload = line["data:".Length..].Trim();
        if (payload == "[DONE]") break;

        using var chunkDoc = JsonDocument.Parse(payload);
        var chunk = chunkDoc.RootElement;

        // pull common fields
        var model = chunk.GetProperty("model").GetString()!;
        var createdSecs = chunk.GetProperty("created").GetInt64();
        var createdAt = DateTimeOffset
                          .FromUnixTimeSeconds(createdSecs)
                          .UtcDateTime
                          .ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";

        // choice & delta
        var choice = chunk.GetProperty("choices")[0];
        var delta  = choice.GetProperty("delta");
        var content = delta.TryGetProperty("content", out var c)
                      ? c.GetString() ?? ""
                      : "";

        // finish_reason
        var finishTok = choice.GetProperty("finish_reason");
        var isDone    = finishTok.ValueKind != JsonValueKind.Null;

        // Start writing our Ollama object
        using var outMs = new MemoryStream();
        await using (var jw = new Utf8JsonWriter(outMs))
        {
            jw.WriteStartObject();

            jw.WriteString("model",      model);
            jw.WriteString("created_at", createdAt);

            // message:{role,content}
            jw.WritePropertyName("message");
            jw.WriteStartObject();
            jw.WriteString("role",    "assistant");
            jw.WriteString("content", content);
            jw.WriteEndObject();

            if (!isDone)
            {
                // intermediate chunk
                jw.WriteBoolean("done", false);
            }
            else
            {
                // final chunk
                jw.WriteString   ("done_reason", finishTok.GetString());
                jw.WriteBoolean  ("done",        true);

                // pull the usage object if present
                if (chunk.TryGetProperty("usage", out var usage) 
                    && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("total_duration", out var td))
                        jw.WriteNumber("total_duration", td.GetInt64());
                    if (usage.TryGetProperty("load_duration", out var ld))
                        jw.WriteNumber("load_duration",  ld.GetInt64());
                    if (usage.TryGetProperty("prompt_eval_count", out var pec))
                        jw.WriteNumber("prompt_eval_count", pec.GetInt32());
                    if (usage.TryGetProperty("prompt_eval_duration", out var ped))
                        jw.WriteNumber("prompt_eval_duration", ped.GetInt64());
                    if (usage.TryGetProperty("eval_count", out var ec))
                        jw.WriteNumber("eval_count", ec.GetInt32());
                    if (usage.TryGetProperty("eval_duration", out var ed))
                        jw.WriteNumber("eval_duration", ed.GetInt64());
                }
            }

            jw.WriteEndObject();
        }

        // flush the JSON
        var json = Encoding.UTF8.GetString(outMs.ToArray());
        await ctx.Response.WriteAsync(json + "\n");
        await ctx.Response.Body.FlushAsync();

        if (isDone) break;
    }
});



// 4) /api/embed     → remote /api/embed
app.MapPost("/api/embed",    ctx => Proxy(ctx, httpClient, "/api/embed"));

// 5) /api/version   → remote /api/version  (if supported)
app.MapGet("/api/version",   ctx => Proxy(ctx, httpClient, "/api/version"));

// 6) /   → remote /  (if supported)
app.MapGet("/",   ctx => Proxy(ctx, httpClient, "/ollama/"));

app.Run("http://*:4222");