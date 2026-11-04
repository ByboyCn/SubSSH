// OAuth 服务全量实现（移植 SubSSH internal/oauthserver/server.go + handlers.go）。
// 端点：/.well-known/oauth-authorization-server、/.well-known/oauth-protected-resource/mcp、
// /oauth/register、/oauth/authorize（信息 + 决策）、/oauth/token（PKCE S256 + refresh_token）。
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.OAuth;

/// <summary>OAuth 协议错误（对应 Go requestError）：映射为 400 + {"error": code, "error_description": ...}。</summary>
internal sealed class OAuthRequestError(string code, string description) : Exception(description)
{
    internal string Code { get; } = code;
    internal string Description { get; } = description;
}

public sealed class OAuthService : IOAuthServer
{
    private const string SupportedScope = "mcp";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    private readonly string _publicUrl;
    private readonly byte[] _masterKeyBytes;
    private readonly SqliteOAuthStore _store;

    public OAuthService(string publicUrl, string masterKey, SqliteOAuthStore store)
    {
        // 对应原版 New()：PublicURL 必须是无路径、查询和片段的 http(s) 来源地址，规范化大小写。
        publicUrl = publicUrl.Trim().TrimEnd('/');
        if (publicUrl.Length != 0)
        {
            if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var u)
                || (u.Scheme != "http" && u.Scheme != "https")
                || u.Host.Length == 0 || u.UserInfo.Length != 0
                || u.Query.Length != 0 || u.Fragment.Length != 0
                || (u.AbsolutePath.Length != 0 && u.AbsolutePath != "/"))
                throw new InvalidOperationException("SUBSSH_PUBLIC_URL 必须是无路径、查询和片段的 http(s) 来源地址");
            _publicUrl = NormalizeOrigin(u);
        }
        else
        {
            _publicUrl = "";
        }
        _masterKeyBytes = Encoding.UTF8.GetBytes(masterKey);
        _store = store;
    }

    public string PublicUrl => _publicUrl;

    // ---- 基地址 ----

    public string BaseUrl(OAuthRequestContext request)
    {
        if (_publicUrl.Length != 0) return _publicUrl;
        var scheme = request.Scheme;
        if (scheme != "https")
        {
            var forwarded = (request.ForwardedProto ?? "").Split(',')[0].Trim();
            if (forwarded is "http" or "https") scheme = forwarded;
            else scheme = "http";
        }
        return scheme + "://" + request.Host.ToLowerInvariant();
    }

    public (string Resource, string MetadataUrl) ResourceUrls(OAuthRequestContext request)
    {
        var base_ = BaseUrl(request);
        return (base_ + "/mcp", base_ + "/.well-known/oauth-protected-resource/mcp");
    }

    // ---- 元数据端点 ----

    public OAuthResponse AuthorizationServerMetadata(OAuthRequestContext request)
    {
        var base_ = BaseUrl(request);
        return Metadata(new Dictionary<string, object>
        {
            ["issuer"] = base_,
            ["authorization_endpoint"] = base_ + "/oauth/authorize",
            ["token_endpoint"] = base_ + "/oauth/token",
            ["registration_endpoint"] = base_ + "/oauth/register",
            ["scopes_supported"] = new[] { SupportedScope },
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["token_endpoint_auth_methods_supported"] = new[] { "none" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
        });
    }

    public OAuthResponse ProtectedResourceMetadata(OAuthRequestContext request)
    {
        var (resource, _) = ResourceUrls(request);
        return Metadata(new Dictionary<string, object>
        {
            ["resource"] = resource,
            ["authorization_servers"] = new[] { BaseUrl(request) },
            ["scopes_supported"] = new[] { SupportedScope },
            ["bearer_methods_supported"] = new[] { "header" },
            ["resource_name"] = "SubSSH MCP",
        });
    }

    private static OAuthResponse Metadata(Dictionary<string, object> value) => new()
    {
        Headers = { ["Content-Type"] = "application/json", ["Cache-Control"] = "public, max-age=3600" },
        Json = value,
    };

    private static OAuthResponse JsonOk(int status, object value) => new()
    {
        Status = status,
        Headers = { ["Content-Type"] = "application/json" },
        Json = value,
    };

    private static OAuthResponse JsonNoStore(int status, object value)
    {
        var r = JsonOk(status, value);
        r.Headers["Cache-Control"] = "no-store";
        return r;
    }

    internal static OAuthResponse JsonError(int status, string code, string description) =>
        JsonNoStore(status, new Dictionary<string, string> { ["error"] = code, ["error_description"] = description });

    private static OAuthResponse TokenError(string code, string description) =>
        JsonError(code == "server_error" ? 500 : 400, code, description);

    private static OAuthResponse ApiError(int status, string description) =>
        JsonOk(status, new Dictionary<string, string> { ["error"] = description });

    // ---- 动态客户端注册 ----

    private sealed record ClientRegistrationRequest(
        [property: JsonPropertyName("redirect_uris")] List<string> RedirectUris,
        [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
        [property: JsonPropertyName("grant_types")] List<string> GrantTypes,
        [property: JsonPropertyName("response_types")] List<string> ResponseTypes,
        [property: JsonPropertyName("client_name")] string ClientName,
        [property: JsonPropertyName("client_uri")] string ClientUri);

    public async Task<OAuthResponse> RegisterClientAsync(string jsonBody, CancellationToken ct = default)
    {
        ClientRegistrationRequest input;
        try
        {
            input = JsonSerializer.Deserialize<ClientRegistrationRequest>(jsonBody, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return JsonError(400, "invalid_client_metadata", "注册信息不是有效 JSON");
        }

        if (input.RedirectUris.Count is 0 or > 10)
            return JsonError(400, "invalid_redirect_uri", "redirect_uris 必须包含 1 到 10 个地址");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var redirectUris = new List<string>(input.RedirectUris.Count);
        foreach (var redirectUri in input.RedirectUris)
        {
            try { ValidateRedirectUri(redirectUri); }
            catch (OAuthRequestError e) { return JsonError(400, "invalid_redirect_uri", e.Description); }
            if (!seen.Add(redirectUri)) continue;
            redirectUris.Add(redirectUri);
        }
        if (input.TokenEndpointAuthMethod.Length != 0 && input.TokenEndpointAuthMethod != "none")
            return JsonError(400, "invalid_client_metadata", "仅支持公共客户端 token_endpoint_auth_method=none");
        if (input.GrantTypes.Count > 0 && !input.GrantTypes.Contains("authorization_code"))
            return JsonError(400, "invalid_client_metadata", "grant_types 必须包含 authorization_code");
        if (input.ResponseTypes.Count > 0 && !input.ResponseTypes.Contains("code"))
            return JsonError(400, "invalid_client_metadata", "response_types 必须包含 code");

        var clientName = input.ClientName.Trim();
        if (clientName.Length == 0) clientName = "未命名 MCP 客户端";
        if (clientName.Length > 200)
            return JsonError(400, "invalid_client_metadata", "client_name 过长");
        try { ValidateOptionalUri(input.ClientUri); }
        catch (OAuthRequestError) { return JsonError(400, "invalid_client_metadata", "client_uri 无效"); }

        string clientId;
        try { clientId = RandomValue("osc_", 24); }
        catch (Exception) { return JsonError(500, "server_error", "无法生成客户端标识"); }

        OAuthClient client;
        try
        {
            client = await _store.CreateOAuthClientAsync(
                new OAuthClient(clientId, clientName, input.ClientUri, redirectUris, 0), ct);
        }
        catch (Exception)
        {
            return JsonError(500, "server_error", "无法保存客户端");
        }

        var r = JsonNoStore(201, new Dictionary<string, object>
        {
            ["client_id"] = client.ClientId,
            ["client_id_issued_at"] = client.CreatedAt,
            ["client_name"] = client.ClientName,
            ["client_uri"] = client.ClientUri,
            ["redirect_uris"] = client.RedirectUris,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
        });
        return r;
    }

    // ---- 授权 ----

    internal sealed record AuthorizationRequest(
        OAuthClient Client, string RedirectUri, string Resource, string Scope,
        string[] Scopes, string State, string CodeChallenge);

    private async Task<AuthorizationRequest> ParseAuthorizationRequestAsync(
        OAuthRequestContext request, IReadOnlyDictionary<string, IReadOnlyList<string>> values, CancellationToken ct)
    {
        if (First(values, "response_type") != "code")
            throw new OAuthRequestError("unsupported_response_type", "仅支持 authorization_code 流程");
        var clientId = First(values, "client_id") ?? "";
        OAuthClient client;
        try { client = await _store.GetOAuthClientAsync(clientId, ct); }
        catch (StoreNotFoundException) { throw new OAuthRequestError("invalid_request", "客户端未注册"); }
        var redirectUri = First(values, "redirect_uri") ?? "";
        if (!client.RedirectUris.Contains(redirectUri))
            throw new OAuthRequestError("invalid_request", "redirect_uri 与客户端注册信息不匹配");
        var challenge = First(values, "code_challenge") ?? "";
        if (!TryDecodeChallenge(challenge) || First(values, "code_challenge_method") != "S256")
            throw new OAuthRequestError("invalid_request", "必须使用 S256 PKCE");
        var (resource, _) = ResourceUrls(request);
        string requestedResource;
        try { requestedResource = NormalizeResource(First(values, "resource") ?? ""); }
        catch (OAuthRequestError) { throw new OAuthRequestError("invalid_target", "resource 必须指向当前 SubSSH MCP 端点"); }
        if (requestedResource != resource)
            throw new OAuthRequestError("invalid_target", "resource 必须指向当前 SubSSH MCP 端点");
        var scopes = ParseScopes(First(values, "scope") ?? "");
        return new AuthorizationRequest(client, redirectUri, resource, string.Join(" ", scopes),
            scopes, First(values, "state") ?? "", challenge);
    }

    private static string[] ParseScopes(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [SupportedScope];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var scope in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (scope != SupportedScope)
                throw new OAuthRequestError("invalid_scope", $"不支持 scope \"{scope}\"");
            if (!seen.Add(scope)) continue;
            list.Add(scope);
        }
        return [.. list];
    }

    private static string NormalizeResource(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)
            || (u.Scheme != "http" && u.Scheme != "https")
            || u.Host.Length == 0 || u.UserInfo.Length != 0
            || u.Fragment.Length != 0 || u.Query.Length != 0)
            throw new OAuthRequestError("invalid_request", "resource 无效");
        return NormalizeOrigin(u) + u.AbsolutePath;
    }

    private static void ValidateRedirectUri(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)
            || u.Scheme.Length == 0 || u.Host.Length == 0 || u.UserInfo.Length != 0 || u.Fragment.Length != 0)
            throw new OAuthRequestError("invalid_request", "redirect_uri 必须是绝对 URL 且不能包含片段");
        if (u.Scheme == "https") return;
        if (u.Scheme != "http")
            throw new OAuthRequestError("invalid_request", "redirect_uri 必须使用 HTTPS，localhost 可使用 HTTP");
        var host = u.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return;
        if (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip))
            throw new OAuthRequestError("invalid_request", "HTTP redirect_uri 仅允许 localhost 或回环地址");
    }

    private static void ValidateOptionalUri(string raw)
    {
        if (raw.Length == 0) return;
        ValidateRedirectUri(raw);
    }

    private static bool ValidCodeVerifier(string value)
    {
        if (value.Length is < 43 or > 128) return false;
        foreach (var c in value)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') && c is not '-' and not '.' and not '_' and not '~')
                return false;
        return true;
    }

    /// <summary>校验 code_challenge：必须能 Base64URL 解码为 32 字节（Go base64.RawURLEncoding）。</summary>
    private static bool TryDecodeChallenge(string challenge)
    {
        try
        {
            var decoded = Base64Url.DecodeFromChars(challenge);
            return decoded.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>对应原版 randomValue：CSPRNG 随机字节的 Base64URL，带前缀。</summary>
    private static string RandomValue(string prefix, int size) =>
        prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(size));

    /// <summary>令牌哈希：HMAC-SHA256，密钥来自 ISettings.MasterKey（原版为普通 SHA-256，见差异报告）。</summary>
    private string TokenHash(string token)
    {
        using var hmac = new HMACSHA256(_masterKeyBytes);
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }

    private static string ExpectedChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    private static string First(IReadOnlyDictionary<string, IReadOnlyList<string>> values, string key) =>
        values.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : "";

    // ---- /oauth/authorize：授权信息 ----

    public async Task<OAuthResponse> AuthorizationInfoAsync(
        OAuthRequestContext request, IReadOnlyDictionary<string, IReadOnlyList<string>> query, CancellationToken ct = default)
    {
        AuthorizationRequest req;
        try { req = await ParseAuthorizationRequestAsync(request, query, ct); }
        catch (OAuthRequestError e) { return RequestError(e); }
        catch (StoreNotFoundException) { return JsonError(400, "invalid_request", "客户端未注册"); }

        List<OAuthHostView> hosts;
        try { hosts = (List<OAuthHostView>)await _store.ListHostViewsAsync(ct); }
        catch (Exception) { return ApiError(500, "无法读取主机列表"); }
        var toolGroups = ToolGroups.All
            .Select(def => (object)new Dictionary<string, object>
            {
                ["name"] = def.Group,
                ["tools"] = def.Tools,
            })
            .ToArray();

        return JsonOk(200, new Dictionary<string, object>
        {
            ["client_name"] = req.Client.ClientName,
            ["client_uri"] = req.Client.ClientUri,
            ["redirect_uri"] = req.RedirectUri,
            ["requested_scopes"] = req.Scopes,
            ["hosts"] = hosts,
            ["tool_groups"] = toolGroups,
        });
    }

    internal sealed record AuthorizationDecisionInput(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("decision")] string Decision,
        [property: JsonPropertyName("all_hosts")] bool AllHosts,
        [property: JsonPropertyName("manage_hosts")] bool ManageHosts,
        [property: JsonPropertyName("host_ids")] List<long> HostIds,
        [property: JsonPropertyName("disabled_tools")] List<string> DisabledTools);

    public async Task<OAuthResponse> AuthorizationDecisionAsync(
        OAuthRequestContext request, string jsonBody, CancellationToken ct = default)
    {
        AuthorizationDecisionInput input;
        try
        {
            input = JsonSerializer.Deserialize<AuthorizationDecisionInput>(jsonBody, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return ApiError(400, "授权请求不是有效 JSON");
        }

        Dictionary<string, IReadOnlyList<string>> values;
        try
        {
            values = ParseQuery(input.Query.TrimStart('?'));
        }
        catch (Exception)
        {
            return ApiError(400, "授权查询参数无效");
        }
        AuthorizationRequest req;
        try { req = await ParseAuthorizationRequestAsync(request, values, ct); }
        catch (OAuthRequestError e) { return RequestError(e); }
        catch (StoreNotFoundException) { return JsonError(400, "invalid_request", "客户端未注册"); }

        if (input.Decision == "deny")
            return JsonOk(200, new Dictionary<string, string>
            {
                ["redirect_uri"] = AuthorizationRedirect(req, "", "access_denied", "管理员拒绝了授权"),
            });
        if (input.Decision != "approve")
            return ApiError(400, "decision 必须是 approve 或 deny");

        List<long> hostIds;
        if (input.AllHosts)
        {
            hostIds = [];
        }
        else
        {
            try { hostIds = (List<long>)await _store.ValidateHostIdsAsync(input.HostIds, ct); }
            catch (InvalidOperationException e) { return ApiError(400, e.Message); }
            catch (Exception) { return ApiError(400, "无法校验主机"); }
            if (hostIds.Count == 0 && !input.ManageHosts)
                return ApiError(400, "请至少授权一台主机");
        }
        string[] disabledTools;
        try { disabledTools = [.. ToolGroups.NormalizeList(input.DisabledTools)]; }
        catch (OAuthRequestError e) { return ApiError(400, e.Description); }

        string plainCode;
        try { plainCode = RandomValue("osc_code_", 32); }
        catch (Exception) { return ApiError(500, "无法生成授权码"); }

        try
        {
            await _store.CreateOAuthAuthorizationCodeAsync(new OAuthAuthorizationCode
            {
                CodeHash = TokenHash(plainCode),
                ClientId = req.Client.ClientId,
                RedirectUri = req.RedirectUri,
                Resource = req.Resource,
                CodeChallenge = req.CodeChallenge,
                Scope = req.Scope,
                AllHosts = input.AllHosts,
                ManageHosts = input.ManageHosts,
                HostIds = hostIds.ToArray(),
                DisabledTools = disabledTools,
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)CodeLifetime.TotalSeconds,
            }, ct);
        }
        catch (Exception)
        {
            return ApiError(500, "无法保存授权码");
        }
        return JsonOk(200, new Dictionary<string, string> { ["redirect_uri"] = AuthorizationRedirect(req, plainCode, "", "") });
    }

    /// <summary>拼授权跳转 URL（保留 redirect_uri 已有查询参数，与原版一致）。</summary>
    private static string AuthorizationRedirect(AuthorizationRequest req, string code, string errorCode, string description)
    {
        var u = new UriBuilder(req.RedirectUri);
        var existing = ParseQuery(u.Query);
        var parts = new List<string>();
        if (code.Length != 0)
            parts.Add($"code={UrlEncode(code)}");
        else
        {
            parts.Add($"error={UrlEncode(errorCode)}");
            if (description.Length != 0) parts.Add($"error_description={UrlEncode(description)}");
        }
        if (req.State.Length != 0) parts.Add($"state={UrlEncode(req.State)}");
        foreach (var (key, list) in existing)
        {
            // Go url.Values.Set 会覆盖同名键；仅保留未覆盖的原有参数。
            if (key is "code" or "error" or "error_description" or "state") continue;
            foreach (var value in list) parts.Add($"{UrlEncode(key)}={UrlEncode(value)}");
        }
        u.Query = string.Join("&", parts);
        return u.Uri.ToString();
    }

    private static string UrlEncode(string value) => WebUtility.UrlEncode(value);

    private static Dictionary<string, IReadOnlyList<string>> ParseQuery(string query)
    {
        var dict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (query.Length == 0) return dict;
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            key = WebUtility.UrlDecode(key);
            value = WebUtility.UrlDecode(value);
            if (!dict.TryGetValue(key, out var list)) dict[key] = [value];
            else if (list is List<string> mutable) mutable.Add(value);
        }
        return dict;
    }

    // ---- /oauth/token ----

    public async Task<OAuthResponse> TokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct = default)
    {
        var grantType = form.TryGetValue("grant_type", out var g) ? g : "";
        return grantType switch
        {
            "authorization_code" => await ExchangeAuthorizationCodeAsync(form, ct),
            "refresh_token" => await RefreshAccessTokenAsync(form, ct),
            _ => TokenError("unsupported_grant_type", "仅支持 authorization_code 和 refresh_token"),
        };
    }

    private async Task<OAuthResponse> ExchangeAuthorizationCodeAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        var plainCode = form.GetValueOrDefault("code", "");
        var verifier = form.GetValueOrDefault("code_verifier", "");
        if (plainCode.Length == 0 || !ValidCodeVerifier(verifier))
            return TokenError("invalid_grant", "授权码或 PKCE verifier 无效");
        var expectedChallenge = ExpectedChallenge(verifier);
        string resource;
        try { resource = NormalizeResource(form.GetValueOrDefault("resource", "")); }
        catch (OAuthRequestError) { return TokenError("invalid_grant", "授权码校验失败"); }

        string plainToken, plainRefreshToken, grantId;
        try
        {
            plainToken = RandomValue("osh_oauth_", 32);
            plainRefreshToken = RandomValue("osh_refresh_", 48);
            grantId = RandomValue("osg_", 24);
        }
        catch (Exception)
        {
            return TokenError("server_error", "无法生成访问令牌");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OAuthAuthorizationCode code;
        try
        {
            code = await _store.ExchangeOAuthAuthorizationCodeAsync(new OAuthAuthorizationCodeExchange
            {
                CodeHash = TokenHash(plainCode),
                ClientId = form.GetValueOrDefault("client_id", ""),
                RedirectUri = form.GetValueOrDefault("redirect_uri", ""),
                Resource = resource,
                CodeChallenge = expectedChallenge,
                GrantId = grantId,
                AccessTokenName = $"OAuth · {form.GetValueOrDefault("client_id", "")} · {plainToken[^8..]}",
                AccessTokenHash = TokenHash(plainToken),
                RefreshTokenHash = TokenHash(plainRefreshToken),
                Now = now,
                AccessExpiresAt = now + (long)TokenLifetime.TotalSeconds,
                RefreshExpiresAt = now + (long)RefreshLifetime.TotalSeconds,
            }, ct);
        }
        catch (OAuthReuseException)
        {
            return TokenError("invalid_grant", "授权码无效、已使用、被撤销或已过期");
        }
        catch (StoreNotFoundException)
        {
            return TokenError("invalid_grant", "授权码无效、已使用、被撤销或已过期");
        }
        catch (Exception)
        {
            return TokenError("server_error", "令牌签发失败，请稍后重试");
        }
        return TokenResponse(plainToken, plainRefreshToken, code.Scope);
    }

    private async Task<OAuthResponse> RefreshAccessTokenAsync(IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        var plainRefreshToken = form.GetValueOrDefault("refresh_token", "");
        string resource;
        try { resource = NormalizeResource(form.GetValueOrDefault("resource", "")); }
        catch (OAuthRequestError)
        {
            if (plainRefreshToken.Length != 0)
                return TokenError("invalid_grant", "refresh_token 或 resource 无效");
            resource = "";
        }
        if (plainRefreshToken.Length == 0)
            return TokenError("invalid_grant", "refresh_token 或 resource 无效");

        string plainToken, plainRotated;
        try
        {
            plainToken = RandomValue("osh_oauth_", 32);
            plainRotated = RandomValue("osh_refresh_", 48);
        }
        catch (Exception)
        {
            return TokenError("server_error", "无法生成访问令牌");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OAuthRefreshToken token;
        try
        {
            token = await _store.RotateOAuthRefreshTokenAsync(new OAuthRefreshTokenRotation
            {
                TokenHash = TokenHash(plainRefreshToken),
                ClientId = form.GetValueOrDefault("client_id", ""),
                Resource = resource,
                AccessTokenName = $"OAuth · {form.GetValueOrDefault("client_id", "")} · {plainToken[^8..]}",
                AccessTokenHash = TokenHash(plainToken),
                RefreshTokenHash = TokenHash(plainRotated),
                Now = now,
                AccessExpiresAt = now + (long)TokenLifetime.TotalSeconds,
                RefreshExpiresAt = now + (long)RefreshLifetime.TotalSeconds,
            }, ct);
        }
        catch (OAuthReuseException)
        {
            return TokenError("invalid_grant", "refresh_token 无效、已使用、被撤销或已过期");
        }
        catch (StoreNotFoundException)
        {
            return TokenError("invalid_grant", "refresh_token 无效、已使用、被撤销或已过期");
        }
        catch (Exception)
        {
            return TokenError("server_error", "令牌签发失败，请稍后重试");
        }
        return TokenResponse(plainToken, plainRotated, token.Scope);
    }

    private static OAuthResponse TokenResponse(string plainToken, string plainRefreshToken, string scope) => new()
    {
        Headers =
        {
            ["Content-Type"] = "application/json",
            ["Cache-Control"] = "no-store",
            ["Pragma"] = "no-cache",
        },
        Json = new Dictionary<string, object>
        {
            ["access_token"] = plainToken,
            ["refresh_token"] = plainRefreshToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = (long)TokenLifetime.TotalSeconds,
            ["scope"] = scope,
        },
    };

    private static OAuthResponse RequestError(OAuthRequestError e) =>
        JsonError(400, e.Code, e.Description);

    private static string NormalizeOrigin(Uri u)
    {
        var builder = new UriBuilder(u)
        {
            Scheme = u.Scheme.ToLowerInvariant(),
            Host = u.IdnHost.ToLowerInvariant(),
        };
        return builder.Uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // DTO 字段名已通过 JsonPropertyName 与原版 Go json tag 一致（snake_case）。
        PropertyNameCaseInsensitive = true,
    };

    // ---- 骨架接口兼容（无请求上下文时用 PublicURL） ----

    object IOAuthService.ProtectedResourceMetadata()
        => ProtectedResourceMetadata(new OAuthRequestContext("http", "", null)).Json!;

    object IOAuthService.AuthorizationServerMetadata()
        => AuthorizationServerMetadata(new OAuthRequestContext("http", "", null)).Json!;
}
