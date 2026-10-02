using Anthropic.SDK;
using OpenAI;
using System.ClientModel;

namespace AIShikikan.Core.Services.Llm;

/// <summary>通过各家 Provider 的官方 API 自动获取模型列表。</summary>
public static class ModelListService
{
    /// <summary>模型列表末段路径。</summary>
    private const string ModelsSegment = "models";

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

    /// <summary>
    /// 从 base_url 推导 OpenAI 兼容的模型列表地址; 无法推导时返回 null。
    /// 约定: base_url 是"目录", 客户端按相对路径拼接(chat/completions、models), 因此
    /// - 末段已是版本段(v1 / v1beta / 2024-10-21): 直接补 /models, 不会出现 /v1/v1 双重前缀;
    /// - 末段不是版本段(漏写版本, 如 http://localhost:8000): 补 /v1/models 兜底;
    /// - 已填到 /models 末段: 原样返回, 不再追加;
    /// - Azure 部署端点(https://xxx.openai.azure.com/openai/deployments/xxx)形态各异, 返回 null。
    /// </summary>
    public static string? BuildModelsUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        var b = baseUrl.Trim().TrimEnd('/');
        if (b.Length == 0 || IsAzureEndpoint(b)) return null;

        if (b.EndsWith("/" + ModelsSegment, StringComparison.OrdinalIgnoreCase)) return b;

        return HasVersionSegment(b) ? $"{b}/{ModelsSegment}" : $"{b}/v1/{ModelsSegment}";
    }

    /// <summary>base_url 是否为 Azure OpenAI 部署端点。</summary>
    public static bool IsAzureEndpoint(string? baseUrl) =>
        !string.IsNullOrEmpty(baseUrl) && baseUrl.Contains("azure.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>判断 URL 最后一段路径是否为 API 版本段(v1 / v1beta / 数字 / 日期版本)。</summary>
    private static bool HasVersionSegment(string url)
    {
        var lastSlash = url.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash == url.Length - 1) return false;

        var seg = url[(lastSlash + 1)..];
        // v1 / V1 / v1beta / v1_2024-05-13
        if (seg.Length > 1 && (seg[0] is 'v' or 'V') && char.IsAsciiDigit(seg[1])) return true;

        // 纯数字版本(1 / 2)与日期版本(2024-10-21)
        return seg.All(c => char.IsAsciiDigit(c) || c is '-' or '_' or '.');
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
        var modelsUrl = BuildModelsUrl(provider.BaseUrl);
        if (modelsUrl is null)
        {
            throw new LlmApiException(IsAzureEndpoint(provider.BaseUrl)
                ? "Azure OpenAI 不支持 /models 自动获取模型列表，请手动填写"
                : $"无法从基础 URL 推导模型列表地址: '{provider.BaseUrl}'");
        }

        // 官方 SDK 会在 Endpoint 之后追加相对路径 "models", 这里传入去掉末段 models 的目录
        var client = new OpenAIClient(
            new ApiKeyCredential(provider.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(modelsUrl[..^ModelsSegment.Length]) });

        var result = await client.GetOpenAIModelClient().GetModelsAsync(ct);
        return result.Value
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
