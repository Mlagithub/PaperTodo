using System.Text.Json;

namespace PaperTodo.Plugin.LlmBridge;

internal sealed record LlmBridgeSettings(
    string BaseUrl,
    string ApiKey,
    string Model,
    string SystemPrompt,
    double Temperature,
    int TimeoutSeconds,
    bool AutoStart)
{
    internal const string DefaultBaseUrl = "https://api.openai.com/v1";
    internal const string DefaultModel = "gpt-4o-mini";
    internal const double TemperatureDisabled = -1;
    internal const int DefaultTimeoutSeconds = 240;

    internal static LlmBridgeSettings Read(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = document.RootElement;
        return new LlmBridgeSettings(
            Text(root, "baseUrl", DefaultBaseUrl, allowEmpty: false),
            Text(root, "apiKey", string.Empty, allowEmpty: true),
            Text(root, "model", DefaultModel, allowEmpty: true),
            Text(root, "systemPrompt", string.Empty, allowEmpty: true),
            Number(root, "temperature", TemperatureDisabled),
            Int(root, "timeoutSeconds", DefaultTimeoutSeconds),
            Bool(root, "autoStart", fallback: true));
    }

    private static string Text(JsonElement root, string name, string fallback, bool allowEmpty)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return fallback;
        var text = (value.GetString() ?? string.Empty).Trim();
        return allowEmpty || !string.IsNullOrWhiteSpace(text) ? text : fallback;
    }

    private static double Number(JsonElement root, string name, double fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return fallback;
        return value.TryGetDouble(out var number) ? number : fallback;
    }

    private static int Int(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return fallback;
        return value.TryGetInt32(out var number) ? number : fallback;
    }

    private static bool Bool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        // Malformed permission values must never turn a saved opt-out into an opt-in.
        return value.ValueKind == JsonValueKind.True;
    }
}

internal static class LlmBridgeText
{
    internal static bool IsChinese(string? uiLanguage) =>
        !string.IsNullOrWhiteSpace(uiLanguage) && uiLanguage.TrimStart().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    internal static string For(string? uiLanguage, string zh, string en) =>
        IsChinese(uiLanguage) ? zh : en;
}
