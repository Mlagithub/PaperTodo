using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace PaperTodo.Plugin.LlmBridge;

/// <summary>
/// Thin OpenAI-compatible <c>/chat/completions</c> client. One static HttpClient per process;
/// the per-request timeout comes from plugin settings via a linked cancellation token.
/// </summary>
internal sealed class LlmChatClient
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            // Local gateways (Ollama, vLLM, one-api) are common callers; do not resolve through
            // a system proxy by accident, but keep the default behavior for portability.
            UseProxy = true
        };
        var client = new HttpClient(handler)
        {
            // Real timeouts are enforced per request from plugin settings.
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PaperTodo-LlmBridge/1.0");
        return client;
    }

    internal static async Task<string> SendChatAsync(
        LlmBridgeSettings settings,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 900)));

        using var request = new HttpRequestMessage(HttpMethod.Post, LlmChatRequest.BuildEndpoint(settings.BaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        request.Content = new StringContent(
            LlmChatRequest.BuildChatRequestBody(settings.Model, settings.SystemPrompt, userPrompt, settings.Temperature),
            Encoding.UTF8,
            "application/json");

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
        var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmApiException(
                LlmChatRequest.ParseErrorResponse((int)response.StatusCode, body));
        }

        return LlmChatRequest.ParseSuccessResponse(body);
    }
}
