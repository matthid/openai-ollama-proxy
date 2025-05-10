namespace http_proxy;

public class OpenAiChatStream
{

    public static async Task HandleApiChat(HttpClient httpClient, HttpContext ctx)
    {
        await ProxyHelper.Proxy(ctx, httpClient, "/api/chat/completions", logData: true);
    }
}