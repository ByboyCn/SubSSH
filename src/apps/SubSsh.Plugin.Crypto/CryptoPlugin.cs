// SubSsh.Plugin.Crypto — 密码箱组件插件（可插拔、可任意替换：换同接口插件即可替换加密实现）。
// GPL-3.0，移植自 SubSSH internal/cryptox（密文格式兼容）。
using System.Security.Cryptography;
using System.Text;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.Crypto;

[Plugin("crypto", "密码箱组件：AES-256-GCM，密文格式与 SubSSH 原版兼容（可被同接口插件替换）")]
public sealed class CryptoPlugin : IComponent
{
    public string Name => "crypto";

    public Task StartAsync(IContext ctx, CancellationToken ct = default)
    {
        AesGcmCryptoBox? box = null;
        ctx.Require<ISettings>(
            s =>
            {
                // 空间可组合：设置上线即建箱发布；重复激活时复用已有实例
                if (box != null)
                {
                    ctx.Provide<ICryptoBox>(box);
                    return;
                }
                box = new AesGcmCryptoBox(DeriveKey(s.MasterKey));
                // 时间可组合：释放 AesGcm 登记为可撤销效果，卸载时自动执行
                ctx.Track(
                    apply: () => { },
                    revert: () => { box.Dispose(); });
                ctx.Provide<ICryptoBox>(box);
            },
            () =>
            {
                // 提供者下线：静默撤回发布，等待设置插件回来
                ctx.Unprovide<ICryptoBox>();
            });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 密钥派生：Go 原版要求 ISettings.MasterKey 为 64 位十六进制字符串
    /// （SUBSSH_MASTER_KEY，openssl rand -hex 32 生成），原样 hex 解码为 32 字节；
    /// 非 hex 输入（如口令式配置）降级为 UTF-8 的 SHA-256 摘要，仍是确定性 32 字节。
    /// </summary>
    private static byte[] DeriveKey(string masterKey)
    {
        if (masterKey.Length == 64)
        {
            try
            {
                return Convert.FromHexString(masterKey);
            }
            catch (FormatException) { }
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(masterKey));
    }
}
