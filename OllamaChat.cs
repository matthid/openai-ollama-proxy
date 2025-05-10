using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace http_proxy;

public class OllamaChat
{
    public static async Task HandleApiChat(HttpClient httpClient, HttpContext ctx)
    {

        // 1) Buffer the incoming JSON so we can inject "stream": true
        using var msIn = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(msIn);
        var inJson = Encoding.UTF8.GetString(msIn.ToArray());

        using var inDoc = JsonDocument.Parse(inJson);
        var root = inDoc.RootElement;

        string? model = null;

        // 2) Re-serialize, copying all props and forcing stream=true
        using var msBody = new MemoryStream();
        await using (var w = new Utf8JsonWriter(msBody))
        {
            w.WriteStartObject();
            var hasStream = false;
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name == "stream")
                {
                    hasStream = true;
                }

                if (p.Name == "model")
                {
                    model = p.Value.GetString();
                }

                if (p.Name == "keep_alive" || p.Name == "options")
                {
                    // TODO: how to do this in ollama?
                    continue;
                }

                p.WriteTo(w);
            }

            if (!hasStream)
            {
                w.WriteBoolean("stream", true);
            }

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
        ctx.Response.StatusCode = (int)upResp.StatusCode;
        if (upResp.StatusCode != HttpStatusCode.OK)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            var error = await upResp.Content.ReadAsStringAsync();
            Console.Error.WriteLine("Upstream responded with:" + error);

            await JsonSerializer.SerializeAsync(ctx.Response.Body,
                new { error = "some error occured", details = error });
            return;
        }

        ctx.Response.ContentType = "application/x-ndjson";
        //ctx.Response.Headers["X-Accel-Buffering"] = "no";

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

        var sw = Stopwatch.StartNew();

        // 5) Transform and forward each SSE chunk
        bool doneSent = false;
        await foreach (var line in ReadLines(await upResp.Content.ReadAsStreamAsync()))
        {
            if (doneSent && !string.IsNullOrEmpty(line) && !line.Contains("[DONE]"))
            {
                Console.Error.WriteLine("ADDITIONAL LINE RECEIVED AFTER DONE: " + line);
                continue;
            }

            if (!line.StartsWith("data:")) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") break;

            using var chunkDoc = JsonDocument.Parse(payload);
            var chunk = chunkDoc.RootElement;

            // pull common fields
            model = chunk.GetProperty("model").GetString()!;
            var createdSecs = chunk.GetProperty("created").GetInt64();
            var createdAt = DateTimeOffset
                .FromUnixTimeSeconds(createdSecs)
                .UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";

            // choice & delta
            var choices = chunk.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
            {
                Console.Error.WriteLine("No choices found: " + chunk);
                continue;
            }

            var choice = chunk.GetProperty("choices")[0];
            var delta = choice.GetProperty("delta");
            var content = delta.TryGetProperty("content", out var c)
                ? c.GetString() ?? ""
                : "";

            // finish_reason
            var finishTok = choice.GetProperty("finish_reason");
            var isDone = finishTok.ValueKind != JsonValueKind.Null;
            if (isDone)
            {
                doneSent = true;
                sw.Stop();
            }

            // Start writing our Ollama object
            string json = await WriteOllamaObject(model, createdAt, content, isDone, finishTok.GetString(), chunk, sw);
            await ctx.Response.WriteAsync(json + (isDone ? "\n\n" : "\n"));
            await ctx.Response.Body.FlushAsync();

        }

        if (!doneSent)
        {
            sw.Stop();
            var createdAt = DateTimeOffset.UtcNow
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "Z";
            string json = await WriteOllamaObject(model ?? "unknown", createdAt,
                "", true, "stop", null, sw);
            await ctx.Response.WriteAsync(json + "\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }


    private static async Task<string> WriteOllamaObject(string mode, string createdAt, string content, bool done, string? finishReason,
        JsonElement? rawRoot, Stopwatch stopwatch)
    {
        using var outMs = new MemoryStream();
        await using (var jw = new Utf8JsonWriter(outMs))
        {
            jw.WriteStartObject();

            jw.WriteString("model", mode);
            jw.WriteString("created_at", createdAt);

            // message:{role,content}
            jw.WritePropertyName("message");
            jw.WriteStartObject();
            jw.WriteString("role", "assistant");
            jw.WriteString("content", content);
            jw.WriteEndObject();

            if (!done)
            {
                // intermediate chunk
                jw.WriteBoolean("done", false);
            }
            else
            {
                // final chunk
                jw.WriteString("done_reason", finishReason);
                jw.WriteBoolean("done", true);

                // pull the usage object if present
                if ((rawRoot?.TryGetProperty("usage", out var usage) ?? false)
                    && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("total_duration", out var td))
                        jw.WriteNumber("total_duration", td.GetInt64());
                    else
                    {
                        jw.WriteNumber("total_duration", (long)stopwatch.Elapsed.TotalNanoseconds);
                    }

                    if (usage.TryGetProperty("load_duration", out var ld))
                        jw.WriteNumber("load_duration", ld.GetInt64());
                    else
                    {
                        jw.WriteNumber("load_duration", 0L);
                    }

                    if (usage.TryGetProperty("prompt_eval_count", out var pec))
                        jw.WriteNumber("prompt_eval_count", pec.GetInt32());
                    else
                    {
                        jw.WriteNumber("prompt_eval_count", 0);
                    }

                    if (usage.TryGetProperty("prompt_eval_duration", out var ped))
                        jw.WriteNumber("prompt_eval_duration", ped.GetInt64());
                    else
                    {
                        jw.WriteNumber("prompt_eval_duration", 0L);
                    }

                    if (usage.TryGetProperty("eval_count", out var ec))
                        jw.WriteNumber("eval_count", ec.GetInt32());
                    else
                    {
                        jw.WriteNumber("eval_count", 0);
                    }

                    if (usage.TryGetProperty("eval_duration", out var ed))
                        jw.WriteNumber("eval_duration", ed.GetInt64());
                    else
                    {
                        jw.WriteNumber("eval_duration", 0L);
                    }
                }
                else
                {
                    jw.WriteNumber("total_duration", (long)stopwatch.Elapsed.TotalNanoseconds);
                    jw.WriteNumber("load_duration", 0L);
                    jw.WriteNumber("prompt_eval_count", 0);
                    jw.WriteNumber("prompt_eval_duration", 0L);
                    jw.WriteNumber("eval_count", 0);
                    jw.WriteNumber("eval_duration", 0L);
                }
            }

            jw.WriteEndObject();
        }

        // flush the JSON
        var json1 = Encoding.UTF8.GetString(outMs.ToArray());
        return json1;
    }
}