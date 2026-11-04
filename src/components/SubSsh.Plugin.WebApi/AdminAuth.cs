// 管理端会话认证：语义对齐原版 internal/webapi/auth.go。
// 会话 cookie：onessh_session = "<exp_unix秒>.<hmac_sha256(exp, key)hex>"，有效期 24h；
// key = SHA256(masterKey ‖ "onessh-admin-session")；密码比较为常量时间。
namespace SubSsh.Plugin.WebApi;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

public sealed class AdminAuth
{
    private const string CookieName = "onessh_session";
    private const int MaxAgeSeconds = 86400;
    private readonly string _password;
    private readonly byte[] _key;

    public AdminAuth(string password, string masterKey)
    {
        _password = password ?? "";
        using var sha = SHA256.Create();
        _key = sha.ComputeHash(Encoding.UTF8.GetBytes((masterKey ?? "") + "onessh-admin-session"));
    }

    /// <summary>常量时间密码比较，失败写 "密码错误" 401（对齐 Login）。</summary>
    public bool CheckPassword(string? input) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(input ?? ""),
            Encoding.UTF8.GetBytes(_password));

    public (string Value, DateTimeOffset Expires) IssueSession(DateTimeOffset now)
    {
        var exp = now.AddHours(24).ToUnixTimeSeconds();
        var msg = exp.ToString();
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(msg));
        return (msg + "." + Convert.ToHexString(mac).ToLowerInvariant(), now.AddSeconds(MaxAgeSeconds));
    }

    /// <summary>对齐 AdminAuth.valid：两段格式、未过期、HMAC 相等。</summary>
    public bool IsValid(string? cookieValue, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookieValue)) return false;
        var parts = cookieValue.Split('.');
        if (parts.Length != 2) return false;
        if (!long.TryParse(parts[0], out var exp) || now.ToUnixTimeSeconds() > exp) return false;
        byte[] got;
        try { got = Convert.FromHexString(parts[1]); }
        catch (FormatException) { return false; }
        var want = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(parts[0]));
        return CryptographicOperations.FixedTimeEquals(got, want);
    }

    /// <summary>设置会话 cookie（HttpOnly、SameSite=Strict、Path=/；HTTPS 判定含 X-Forwarded-Proto）。</summary>
    public void SetSessionCookie(HttpContext ctx)
    {
        var (value, expires) = IssueSession(DateTimeOffset.UtcNow);
        ctx.Response.Cookies.Append(CookieName, value, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = RequestIsHttps(ctx),
            MaxAge = TimeSpan.FromSeconds(MaxAgeSeconds),
            Expires = expires,
        });
    }

    public string? GetSessionCookie(HttpContext ctx) => ctx.Request.Cookies.TryGetValue(CookieName, out var v) ? v : null;

    /// <summary>对齐 requestIsHTTPS：TLS 或首个 X-Forwarded-Proto 为 https。</summary>
    public static bool RequestIsHttps(HttpContext ctx)
    {
        if (ctx.Request.IsHttps) return true;
        var proto = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
        var first = proto.Split(',')[0].Trim();
        return string.Equals(first, "https", StringComparison.OrdinalIgnoreCase);
    }
}
