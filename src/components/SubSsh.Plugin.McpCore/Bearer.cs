// Bearer 令牌校验与授权语义（对应 mcpserver/auth.go）。
// FindTokenForResourceAsync：按哈希查令牌并要求 resource 匹配（manual 令牌豁免）；
// 失败时 HTTP 层须回 WWW-Authenticate: Bearer resource_metadata="…", scope="mcp", error="invalid_token"。
using System.Globalization;
using System.Net;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.McpCore;

/// <summary>通过校验的调用主体（对应 Go Principal：Token + 已授权主机集合 + Store）。</summary>
public sealed record McpPrincipal(ApiToken Token, IReadOnlyDictionary<string, HostConfig> Hosts)
{
    /// <summary>对应 Go McpCallContext.FromContext 语义：令牌 id/name/令牌级 disabled_tools。</summary>
    public McpCallContext ToCallContext() =>
        new(Token.Id, Token.Name, Token.DisabledTools);
}

/// <summary>认证失败异常；Challenge 直接用于 WWW-Authenticate 响应头。</summary>
public sealed class McpUnauthorizedException(string challenge) : Exception("unauthorized")
{
    public string Challenge { get; } = challenge;
}

public sealed class BearerAuthenticator(IOneSshStoreFull store)
{
    /// <summary>
    /// 校验 Authorization 头并解析授权主机集合（对应 Go Bearer 中间件）。
    /// 成功返回 McpPrincipal；失败抛 McpUnauthorizedException（HTTP 层设置 401 + WWW-Authenticate）。
    /// </summary>
    public async Task<McpPrincipal> AuthenticateAsync(string? authorizationHeader, string resource, string metadataUrl, CancellationToken ct = default)
    {
        var fields = SplitAuthorization(authorizationHeader);
        if (fields == null)
            throw Unauthorized(metadataUrl, errorCode: "");
        var (token, hosts) = await store.FindTokenForResourceAsync(fields, resource, ct).ConfigureAwait(false);
        if (token == null)
            throw Unauthorized(metadataUrl, "invalid_token");
        var allowed = hosts.ToDictionary(h => h.Name, h => h, StringComparer.Ordinal);
        return new McpPrincipal(token, allowed);
    }

    /// <summary>对应 Go AuthorizedHostManagement：要求令牌具备 manage_hosts 权限。</summary>
    public static void RequireManageHosts(McpPrincipal? principal)
    {
        if (principal == null) throw new SubSshException("unauthorized");
        if (!principal.Token.ManageHosts) throw new SubSshException("host management not authorized");
    }

    /// <summary>
    /// 主机访问授权（对应 Go AuthorizedHost）：
    /// 在令牌主机集合内直接放行；否则查库区分 unknown host / host archived / host not authorized。
    /// </summary>
    public static async Task<HostConfig> AuthorizedHostAsync(McpPrincipal? principal, IOneSshStoreFull store, string name)
    {
        if (principal == null) throw new SubSshException("unauthorized");
        if (principal.Hosts.TryGetValue(name, out var h)) return h;
        var found = await store.GetHostByNameAsync(name).ConfigureAwait(false);
        if (found == null) throw new SubSshException("unknown host: " + name);
        if (found.ArchivedAt != null)
            throw new SubSshException("host archived: " + name + "（主机已归档，需管理员在控制台取消归档）");
        throw new SubSshException("host not authorized: " + name);
    }

    private static string? SplitAuthorization(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !string.Equals(parts[0], "Bearer", StringComparison.OrdinalIgnoreCase))
            return null;
        return parts[1];
    }

    private static McpUnauthorizedException Unauthorized(string metadataUrl, string errorCode)
    {
        var challenge = string.Format(CultureInfo.InvariantCulture,
            "Bearer resource_metadata=\"{0}\", scope=\"mcp\"", metadataUrl);
        if (errorCode.Length > 0)
            challenge += string.Format(CultureInfo.InvariantCulture, ", error=\"{0}\"", errorCode);
        return new McpUnauthorizedException(challenge);
    }
}
