// JSON-RPC 2.0 消息形状（手写，不用 MCP SDK）。
// 请求：{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{...}}
// 通知：无 id 字段，服务器不回包。
// 成功响应：{"jsonrpc":"2.0","id":1,"result":{...}}
// 错误响应：{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"..."}}
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubSsh.Plugin.McpCore;

/// <summary>对外序列化配置：camelCase、不转义 HTML、无缩进（对外 API/MCP 输出与 Go 原版一致）。</summary>
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>JSON-RPC 错误码（对齐 JSON-RPC 2.0 规范与 MCP 惯例）。</summary>
public static class JsonRpcError
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

/// <summary>解析后的一条入站消息；IsNotification 表示无 id（不回包）。</summary>
public sealed record JsonRpcMessage
{
    public bool IsNotification { get; init; }
    public JsonElement Id { get; init; }
    public string Method { get; init; } = "";
    public JsonElement Params { get; init; }
}

/// <summary>响应构造：成功（result）或错误（error.code/message）。</summary>
public static class JsonRpcResponder
{
    public static string Result(JsonRpcMessage msg, object? result) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = msg.Id.Clone(),
            ["result"] = result,
        }, McpJson.Options);

    public static string Error(JsonRpcMessage msg, int code, string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = msg.IsNotification ? (JsonElement?)null : msg.Id.Clone(),
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        }, McpJson.Options);
}

/// <summary>解析一条 JSON-RPC 文本；非对象或缺 method 时返回 null（由调用方回 ParseError/InvalidRequest）。</summary>
public static class JsonRpcParser
{
    public static JsonRpcMessage? TryParse(string text)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            return null;
        return new JsonRpcMessage
        {
            IsNotification = !root.TryGetProperty("id", out _),
            Id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : default,
            Method = methodEl.GetString() ?? "",
            Params = root.TryGetProperty("params", out var p) ? p.Clone() : default,
        };
    }
}
