using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ryan.MCP.Mcp.Configuration;

namespace Ryan.MCP.Mcp.Services.Knowledge;

public sealed class EmbeddingsService(
    IHttpClientFactory httpClientFactory,
    McpOptions options,
    ILogger<EmbeddingsService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        if (!options.Embeddings.Enabled || string.IsNullOrWhiteSpace(options.Embeddings.ApiKey))
            return null;

        try
        {
            using var http = httpClientFactory.CreateClient("embeddings");
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.Embeddings.ApiKey);

            var baseUrl = options.Embeddings.BaseUrl.TrimEnd('/');
            var body = new
            {
                input = text.Length > 8000 ? text[..8000] : text,
                model = options.Embeddings.Model,
            };

            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await http.PostAsync($"{baseUrl}/embeddings", content, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                logger.LogWarning("Embeddings API returned {Status}: {Body}", (int)response.StatusCode, errBody);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
            var data = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
            return data.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get embedding for text (length {Len})", text.Length);
            return null;
        }
    }
}
