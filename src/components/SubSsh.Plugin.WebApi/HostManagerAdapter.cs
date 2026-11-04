// IHostManager → IWebApiHostManager 适配器：类型转换 + HostManagerException→WebApiHostException。
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.WebApi;

public sealed class HostManagerAdapter(IHostManager inner) : IWebApiHostManager
{
    public Task<WebApiHostView> CreateAsync(WebApiHostInput input, CancellationToken ct = default)
        => Wrap(inner.CreateAsync(ToHostInput(input), ct));

    public Task<WebApiHostView> UpdateAsync(long id, WebApiHostInput input, CancellationToken ct = default)
        => Wrap(inner.UpdateAsync(id, ToHostInput(input), ct));

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        try { await inner.DeleteAsync(id, ct); }
        catch (HostManagerException e) { throw ToWebApi(e); }
    }

    public async Task<WebApiHostTestResult> TestAsync(long id, CancellationToken ct = default)
    {
        try
        {
            var res = await inner.TestAsync(id, ct);
            return res.Output.Length > 0
                ? new WebApiHostTestResult(true, res.Output)
                : new WebApiHostTestResult(true, null);
        }
        catch (Exception e) { return new WebApiHostTestResult(false, e.Message); }
    }

    public Task ResetFingerprintAsync(long id, CancellationToken ct = default)
        => WrapNoResult(inner.ResetFingerprintAsync(id, ct));

    public Task<WebApiHostView> ArchiveAsync(long id, CancellationToken ct = default)
        => Wrap(inner.ArchiveAsync(id, ct));

    public Task<WebApiHostView> UnarchiveAsync(long id, CancellationToken ct = default)
        => Wrap(inner.UnarchiveAsync(id, ct));

    private static HostInput ToHostInput(WebApiHostInput i) => new()
    {
        Name = i.Name,
        Addr = i.Addr,
        Port = i.Port,
        Username = i.Username,
        AuthType = i.AuthType,
        KeyId = i.KeyId,
        Password = i.Password.Length > 0 ? i.Password : null,
        // Web 前端传 jump_host_id（数字），MCP 侧传 jump_host（名称）；此处仅桥接名称型字段为空，
        // 数字跳板由 HostManager 内部按 ID 校验（HostConfig.JumpHostId 语义）。
        JumpHost = null,
        MonitorEnabled = i.MonitorEnabled,
        Tags = i.Tags,
    };

    private static WebApiHostException ToWebApi(HostManagerException e)
        => new(e.Kind switch
        {
            HostManagerErrorKind.Invalid => WebApiHostErrorKind.Invalid,
            HostManagerErrorKind.NotFound => WebApiHostErrorKind.NotFound,
            HostManagerErrorKind.Conflict => WebApiHostErrorKind.Conflict,
            HostManagerErrorKind.Connection => WebApiHostErrorKind.Connection,
            _ => WebApiHostErrorKind.Internal,
        }, e.Message);

    private static async Task<WebApiHostView> Wrap(Task<HostConfig> task)
    {
        try { return ToView(await task); }
        catch (HostManagerException e) { throw ToWebApi(e); }
    }

    private static async Task WrapNoResult(Task task)
    {
        try { await task; }
        catch (HostManagerException e) { throw ToWebApi(e); }
    }

    private static WebApiHostView ToView(HostConfig h) => new()
    {
        Id = h.Id,
        Name = h.Name,
        Addr = h.Addr,
        Port = h.Port,
        Username = h.Username,
        AuthType = h.AuthType,
        KeyId = h.KeyId,
        HostKeyFp = h.HostKeyFp,
        JumpHostId = h.JumpHostId,
        MonitorEnabled = h.MonitorEnabled,
        Tags = h.Tags ?? [],
        CreatedAt = h.CreatedAt,
        Archived = h.ArchivedAt != null,
        ArchivedAt = h.ArchivedAt,
    };
}
