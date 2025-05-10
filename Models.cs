using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace http_proxy;

public class Models
{
    public static async Task HandleApiTags(HttpClient httpClient, HttpContext ctx, MemoryStream? cachedData)
    {
        if (cachedData != null)
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.Body.WriteAsync(cachedData.GetBuffer(), 0, (int)cachedData.Length);
            return;
        }
        
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

        // 4) include each model entry: unwrap provider metadata when available, otherwise include full entry
        var list = new List<JsonElement>();
        foreach (var item in arr.EnumerateArray())
        {
            JsonElement modelEntry;
            
            // ===========================
            // Helper to build `JsonElement` in the ollama shape
            // ===========================
            static JsonElement BuildOllamaElement(string id, long createdEpoch)
            {
                // Convert Unix epoch seconds → ISO 8601 string
                var modifiedAt = DateTimeOffset
                    .FromUnixTimeSeconds(createdEpoch)
                    .ToString("o");
                
                static string HashWithSHA256(string value)
                {
                    using var hash = SHA256.Create();
                    var byteArray = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
                    return Convert.ToHexString(byteArray).ToLowerInvariant();
                }
                // Anonymous object that matches your ollama schema
                var ollamaObj = new
                {
                    name        = id,
                    model       = id,
                    modified_at = modifiedAt,
                    size        = 1000000L,      // fill in if you know it
                    digest      = HashWithSHA256(id),      // same
                    details     = new
                    {
                        parent_model       = "local",
                        format             = "local",
                        family             = "local",
                        families           = new[] { id },
                        parameter_size     = "100b",
                        quantization_level = ""
                    },
                    urls = new[] { 0 }     // placeholder
                };

                // Serialize → parse back into a JsonElement
                var json = JsonSerializer.Serialize(ollamaObj);
                return JsonDocument.Parse(json).RootElement;
            }
            
            // 1) Already an ollama response?  Just unwrap it.
            if (item.TryGetProperty("ollama", out var ollamaVal))
            {
                modelEntry = ollamaVal.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(ollamaVal.GetString()!).RootElement
                    : ollamaVal;
            }
            // 2) Nested OpenAI provider block?  Unwrap and map.
            else if (item.TryGetProperty("openai", out var openAiVal))
            {
               // continue;
                var id           = openAiVal.GetProperty("id").GetString()!;
                var createdEpoch = openAiVal.GetProperty("created").GetInt64();
                modelEntry = BuildOllamaElement(id, createdEpoch);
            }
            // 3) “Flat” other model object (the one with top‐level "id", "created", etc.)
            else if (item.TryGetProperty("id", out var flatId)
                     && item.TryGetProperty("created", out var flatCreated))
            {
               // continue;
                var id           = flatId.GetString()!;
                var createdEpoch = flatCreated.GetInt64();
                modelEntry = BuildOllamaElement(id, createdEpoch);
            }
            // 4) Otherwise give them back exactly as they came in
            else
            {
                Console.Error.WriteLine("Could not convert model: " + item);
                continue;
            }

            list.Add(modelEntry);
        }

        // 5) write back as JSON array of tags (models)
        ctx.Response.ContentType = "application/json";
        var mem = new MemoryStream();
        await JsonSerializer.SerializeAsync(mem, new { models = list });
        cachedData = mem;
        await ctx.Response.Body.WriteAsync(cachedData.GetBuffer(), 0, (int)cachedData.Length);
    }
}