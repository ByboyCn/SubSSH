// SubSsh.Plugin.Memory — embedding 客户端与向量编解码（对应 Go internal/memoryx/embed.go）。
// OpenAI 兼容 /embeddings 协议；向量以 little-endian float32 字节存库（与 Go 版格式兼容）。
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

namespace SubSsh.Plugin.Memory;

/// <summary>embedding 配置（对应 Go EmbeddingConfig）。</summary>
public sealed record EmbeddingConfig(string ApiUrl, string ApiKey, string Model)
{
    /// <summary>对应 Go EmbeddingConfig.Enabled：URL 与 Model 都非空才启用。</summary>
    public bool Enabled() => ApiUrl != "" && Model != "";
}

/// <summary>embedding 客户端（对应 Go Embedder）。</summary>
public sealed class Embedder : IDisposable
{
    private readonly EmbeddingConfig _cfg;
    private readonly HttpClient _client;

    public Embedder(EmbeddingConfig cfg)
    {
        // Go newEmbedder：去掉 URL 末尾的 "/"。
        cfg = cfg with { ApiUrl = cfg.ApiUrl.TrimEnd('/') };
        _cfg = cfg;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public string Model => _cfg.Model;

    public void Dispose() => _client.Dispose();

    /// <summary>文本转向量（对应 Go Embedder.Embed）。</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new EmbeddingRequest(_cfg.Model, [text]));
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _cfg.ApiUrl + "/embeddings") { Content = content };
        if (_cfg.ApiKey != "")
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.ApiKey);
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (message.Length > 4096) message = message[..4096];
            var text2 = Encoding.UTF8.GetString(message).Trim();
            throw new SubSsh.Core.Abstractions.SubSshException(
                $"embedding 服务返回 {(int)response.StatusCode} {response.ReasonPhrase}: {text2}");
        }
        EmbeddingResponse? decoded;
        try
        {
            decoded = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            throw new SubSsh.Core.Abstractions.SubSshException($"解析 embedding 响应: {e.Message}");
        }
        if (decoded is null || decoded.Data.Length == 0 || decoded.Data[0].Embedding.Length == 0)
            throw new SubSsh.Core.Abstractions.SubSshException("embedding 响应缺少向量");
        return decoded.Data[0].Embedding;
    }

    private sealed record EmbeddingRequest(string Model, string[] Input);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] EmbeddingData[] Data);
    private sealed record EmbeddingData([property: JsonPropertyName("embedding")] float[] Embedding);
}

/// <summary>向量编解码（对应 Go encodeVector/decodeVector：little-endian float32）。</summary>
public static class VectorCodec
{
    public static byte[]? Encode(float[] vector)
    {
        if (vector.Length == 0) return null;
        var encoded = new byte[vector.Length * 4];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(i * 4, 4), BitConverter.SingleToUInt32Bits(vector[i]));
        return encoded;
    }

    /// <summary>解码失败（非 4 的倍数）抛出，文案与 Go 一致。</summary>
    public static float[] Decode(byte[] encoded)
    {
        if (encoded.Length % 4 != 0)
            throw new SubSsh.Core.Abstractions.SubSshException($"向量字节长度 {encoded.Length} 不是 4 的倍数");
        var vector = new float[encoded.Length / 4];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(i * 4, 4)));
        return vector;
    }
}
