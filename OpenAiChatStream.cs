using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace http_proxy;

public class OpenAiChatStream
{
    private static bool TryReadJson(MemoryStream stream, [NotNullWhen(true)] out JsonDocument? json)
    {
        var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(stream.GetBuffer(), 0, (int)stream.Length));
        if (JsonDocument.TryParseValue(ref reader, out var doc))
        {
            json = doc;
            return true;
        }

        json = null;
        return false;
    }
    private static T? ReadJsonDataLine<T>(string line)
    {
        var jsonPart = line.AsSpan()["data: ".Length..];
        return JsonSerializer.Deserialize<T>(jsonPart);
    }
    public static async Task HandleApiChat(HttpClient client, HttpContext ctx)
    {
        var path = "/api/chat/completions";

        // build the upstream URI
        var upstream = path + ctx.Request.QueryString;

        // start building our outgoing HttpRequestMessage
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstream);

        JsonDocument? requestDocument = null;
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
            if (TryReadJson(mem, out var json))
            {
                requestDocument = json;
                await Console.Error.WriteLineAsync("----------- JSON REQUEST -----------\n" +
                                                   JsonSerializer.Serialize(requestDocument));
            }
            else
            {
                await Console.Error.WriteLineAsync("----------- RAW REQUEST -----------\n" +
                                                   Encoding.UTF8.GetString(mem.GetBuffer(), 0, (int)mem.Length));
            }
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

        var isStreaming = (requestDocument?.RootElement.TryGetProperty("stream", out var streamElem) ?? false) && streamElem.GetBoolean();
        
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
        var contentEncoding = resp.Content.Headers.ContentEncoding;
        if (isStreaming)
        {
            var reader = ProxyHelper.ReadDecodedLines(contentEncoding, await resp.Content.ReadAsStreamAsync());
            var writer = new StreamWriter(ctx.Response.Body);
            bool lastWasToolCall = false;
            bool finishSent = false;
            ChatCompletionChunkDto? lastChunk = null;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrEmpty(line))
                {
                    await writer.WriteLineAsync(line);
                }
                else if (line.Contains("[DONE]"))
                {
                    if (!finishSent)
                    {
                        var stopChunk = new ChatCompletionChunkDto()
                        {
                            Choices = new List<ChoiceDto?>()
                            {
                                new ChoiceDto()
                                {
                                    FinishReason = "stop",
                                    Delta = new DeltaDto(),
                                    Index = 0
                                }
                            },
                            SystemFingerprint = "fp_a1102cf978",
                            Object = "chat.completion.chunk",
                            Model = lastChunk?.Model ?? "unknown",
                            Id = lastChunk?.Id ?? "unknown",
                            Created = lastChunk?.Created ?? 1747166839,
                            ServiceTier = lastChunk?.ServiceTier ?? "default",
                        };
                        await writer.WriteAsync("data: ");
                        await writer.FlushAsync();
                        await Console.Error.WriteLineAsync("---- STOP ----\n" + JsonSerializer.Serialize(stopChunk));
                        await JsonSerializer.SerializeAsync(writer.BaseStream, stopChunk);
                        await writer.FlushAsync();
                        await writer.WriteLineAsync();
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();
                    }
                    
                    await Console.Error.WriteLineAsync("---- DONE ----\n" + line);
                    await writer.WriteLineAsync(line);
                }
                else if (line.StartsWith("data: "))
                {
                    await Console.Error.WriteLineAsync("---- RAW RESPONSE LINE ----\n" + line);
                    var chunk = ReadJsonDataLine<ChatCompletionChunkDto>(line);
                    if (chunk is null)
                    {
                        throw new InvalidOperationException(
                            $"Could not parse ChatCompletionChunkDto from line '{line}'");
                    }

                    lastChunk = chunk;

                    await Console.Error.WriteLineAsync("---- Parsed Chunk RESPONSE LINE ----\n" + JsonSerializer.Serialize(chunk));
                    

                    if (chunk.ServiceTier is null)
                    {
                        chunk.ServiceTier = "default";
                    }

                    if (chunk.SystemFingerprint is null)
                    {
                        chunk.SystemFingerprint = "fp_a1102cf978";
                    }
                    
                    if (chunk.Choices?.Count > 0 && chunk.Choices[0] is {} firstChoice)
                    {
                        if (firstChoice.FinishReason == "stop")
                        {
                            finishSent = true;
                            if (lastWasToolCall)
                            {
                                firstChoice.FinishReason = "tool_calls";
                            }
                        }
                        
                        if (firstChoice.Delta?.ToolCalls?.Count > 0)
                        {
                            if (firstChoice.Delta.Role is null)
                            {
                                firstChoice.Delta.Role = "assistant";
                            }
                        
                            var argument = firstChoice.Delta.ToolCalls[0].Function?.Arguments;
                            if (!string.IsNullOrEmpty(argument) && firstChoice.Delta.ToolCalls[0] is {} firstToolCall && firstToolCall.Function is {})
                            {
                                // Split up tool call
                                firstToolCall.Function.Arguments = "";
                                
                                await writer.WriteAsync("data: ");
                                await writer.FlushAsync();
                                await Console.Error.WriteLineAsync("---- Modified chunk RESPONSE LINE (split) ----\n" + JsonSerializer.Serialize(chunk));
                                await JsonSerializer.SerializeAsync(writer.BaseStream, chunk);
                                await writer.FlushAsync();
                                await writer.WriteLineAsync();
                                await writer.WriteLineAsync();
                                
                                
                                // Split 2
                                firstToolCall.Function.Name = null;
                                firstToolCall.Function.Arguments = argument;
                                firstToolCall.Id = null;
                                firstToolCall.Type = null;
                                firstChoice.Delta.Role = null;
                            }
                            
                            lastWasToolCall = true;
                        }
                        else
                        {
                            lastWasToolCall = false;
                        }
                    }
                    else
                    {
                        lastWasToolCall = false;
                    }

                    await Console.Error.WriteLineAsync("---- Modified chunk RESPONSE LINE ----\n" + JsonSerializer.Serialize(chunk));
                    await writer.WriteAsync("data: ");
                    await writer.FlushAsync();
                    await JsonSerializer.SerializeAsync(writer.BaseStream, chunk);
                    await writer.FlushAsync();
                    await writer.WriteLineAsync();
                }
                else
                {
                    await Console.Error.WriteLineAsync("---- RAW RESPONSE LINE (decompressed) ----\n" + line);
                    await writer.WriteLineAsync(line);
                }
                
                await writer.FlushAsync();
            }
        }
        else
        {
            var pipe = new Pipe();
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
}