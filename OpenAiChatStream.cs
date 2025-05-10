using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace http_proxy;

public class OpenAiChatStream
{
    public static async Task HandleApiChat(HttpClient client, HttpContext ctx)
    {
        var path = "/api/chat/completions";

        // build the upstream URI
        var upstream = path + ctx.Request.QueryString;

        // start building our outgoing HttpRequestMessage
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstream);

        // if there's a request body, attach it as stream content
        if (ctx.Request.ContentLength.GetValueOrDefault() > 0
            || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            // wrap the raw request body in StreamContent
            StreamContent sc;
            var mem = new MemoryStream();
            ctx.Request.EnableBuffering();
            await ctx.Request.Body.CopyToAsync(mem);
            mem.Position = 0;
            await Console.Error.WriteLineAsync("----------- RAW REQUEST -----------\n" +
                                               Encoding.UTF8.GetString(mem.GetBuffer(), 0, (int)mem.Length));
            sc = new StreamContent(mem);
           
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
        var pipe = new Pipe();
        var contentEncoding = resp.Content.Headers.ContentEncoding;
        Task logTask;
        {
            await using var pipeWriter = pipe.Writer.AsStream();
            logTask = Task.Run(async () =>
            {
                await using var pipeReader = pipe.Reader.AsStream();
                var reader = ProxyHelper.ReadDecodedLines(contentEncoding, pipeReader);
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (!string.IsNullOrEmpty(line))
                        Console.Error.WriteLine("---- RAW RESPONSE LINE (decompressed) ----\n" + line);
                }
            });

            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int n;
                var stream = await resp.Content.ReadAsStreamAsync();
                while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    // 6a) to the client
                    await ctx.Response.Body.WriteAsync(buffer, 0, n);
                    // 6b) into our tap buffer
                    await pipeWriter.WriteAsync(buffer, 0, n);
                }

                pipeWriter.Flush();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        await logTask;
    }
}