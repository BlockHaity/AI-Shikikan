using Anthropic.SDK;
using OpenAI;
using System.ClientModel;

namespace AIShikikan.Core.Services.Llm;

/// <summary>通过各家 Provider 的官方 API 自动获取模型列表。</summary>
public static class ModelListService
{
    public static async Task<IReadOnlyList<string>> FetchModelsAsync(
        ProviderConfig provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new LlmApiException("缺少 API Key，无法获取模型列表");
        }

        return provider.Kind == ProviderKind.Anthropic
            ? await FetchAnthropicAsync(provider, ct)
            : await FetchOpenAiAsync(provider, ct);
    }

    private static async Task<IReadOnlyList<string>> FetchAnthropicAsync(
        ProviderConfig provider, CancellationToken ct)
    {
        var client = new AnthropicClient(
            new APIAuthentication(provider.ApiKey),
            new HttpClient { Timeout = TimeSpan.FromMinutes(2) });

        var baseUrl = provider.BaseUrl.TrimEnd('/');
        if (!baseUrl.Equals("https://api.anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            client.ApiUrlFormat = $"{baseUrl}/{{0}}/{{1}}";
        }

        var result = await client.Models.ListModelsAsync(limit: 100, ctx: ct);
        return result.Models.Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> FetchOpenAiAsync(
        ProviderConfig provider, CancellationToken ct)
    {
        if (provider.BaseUrl.Contains("azure.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new LlmApiException("Azure OpenAI 不支持 /models 自动获取模型列表，请手动填写");
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(provider.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(provider.BaseUrl.TrimEnd('/') + "/") });

        var result = await client.GetOpenAIModelClient().GetModelsAsync(ct);
        return result.Value
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
