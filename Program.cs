// Program.cs (.NET 7+ minimal API)

using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using http_proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.SmallestSize;
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

var app     = builder.Build();

app.UseResponseCompression();

// ————— CONFIG —————
var remoteBase = Environment.GetEnvironmentVariable("OPENWEBUI_ENDPOINT");
var apiKey     = Environment.GetEnvironmentVariable("OPENWEBUI_API_KEY");
// ——————————————————

var httpClient = new HttpClient { BaseAddress = new Uri(remoteBase) };
httpClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", apiKey);



// 1) /api/tags  → remote GET /api/models  → Ollama shape
MemoryStream? cachedData = null;
app.MapGet("/api/tags", async ctx => await Models.HandleApiTags(httpClient, ctx, cachedData));

// 2) /api/generate  → remote /api/generate
app.MapPost("/api/generate", ctx => ProxyHelper.Proxy(ctx, httpClient, "/api/generate"));

// 3) /api/chat      → remote /api/chat/completions
// Replace your old /api/chat proxy with this:
app.MapPost("/api/chat", async ctx =>
{
    await OllamaChat.HandleApiChat(httpClient, ctx);
});



// 4) /api/embed     → remote /api/embed
app.MapPost("/api/embed",    ctx => ProxyHelper.Proxy(ctx, httpClient, "/api/embed"));

// 5) /api/version   → remote /api/version  (if supported)
app.MapGet("/api/version",   ctx => ProxyHelper.Proxy(ctx, httpClient, "/api/version"));

// 6) /   → remote /  (if supported)
//var statusCache = new MemoryStream();
app.MapGet("/",  async ctx =>
{
    await ctx.Response.WriteAsync("Ollama is running");
    await ctx.Response.Body.FlushAsync();
});

app.MapPost("/openai/api/chat/completions",   ctx => OpenAiChatStream.HandleApiChat(httpClient, ctx));
app.MapGet("/openai/api/models",   ctx => ProxyHelper.Proxy(ctx, httpClient, "/api/models", logData: true));


app.Run("http://*:4222");
