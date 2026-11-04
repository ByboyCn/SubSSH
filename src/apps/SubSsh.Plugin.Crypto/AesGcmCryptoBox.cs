// SubSsh.Plugin.Crypto — AES-256-GCM 密码箱（cryptox.go 等价，密文格式与原版兼容）。
// GPL-3.0，移植自 SubSSH internal/cryptox/cryptox.go。
using System.Security.Cryptography;
using SubSsh.Core.Abstractions;
using SubSsh.Core.Composition;

namespace SubSsh.Plugin.Crypto;

/// <summary>
/// AesGcm 等价实现（对应 Go Box：32 字节密钥、12 字节随机 nonce 前置、16 字节 tag 后缀）。
/// </summary>
public sealed class AesGcmCryptoBox : ICryptoBox, IDisposable
{
    /// <summary>对应 Go cipher.NewGCM 的 NonceSize（标准 12 字节）。</summary>
    public const int NonceSize = 12;
    /// <summary>GCM tag 长度（Go cipher.NewGCM 默认 16 字节）。</summary>
    public const int TagSize = 16;
    /// <summary>对应 Go New 的 32 字节主密钥要求。</summary>
    public const int KeySize = 32;

    private readonly AesGcm _aead;

    public AesGcmCryptoBox(byte[] key)
    {
        if (key.Length != KeySize)
            throw new SubSshException("AES-256-GCM 主密钥必须为 32 字节");
        // tagSizeInBytes 构造参数在 .NET 8+ 必填，16 与 Go 默认一致
        _aead = new AesGcm(key, TagSize);
    }

    /// <summary>对应 Go Box.Seal：随机 nonce 前置，输出 nonce || ciphertext || tag。</summary>
    public Task<byte[]> SealAsync(byte[] plain, CancellationToken ct = default)
    {
        var blob = new byte[NonceSize + plain.Length + TagSize];
        RandomNumberGenerator.Fill(blob.AsSpan(0, NonceSize));
        _aead.Encrypt(blob.AsSpan(0, NonceSize), plain, blob.AsSpan(NonceSize), null);
        return Task.FromResult(blob);
    }

    /// <summary>对应 Go Box.Open：blob 前 12 字节为 nonce，其余为 ciphertext || tag。</summary>
    public Task<byte[]> OpenAsync(byte[] blob, CancellationToken ct = default)
    {
        if (blob.Length < NonceSize + TagSize)
            throw new SubSshException("密文长度无效");
        var plain = new byte[blob.Length - NonceSize - TagSize];
        try
        {
            _aead.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize), plain, null);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SubSshException("解密失败: " + ex.Message);
        }
        return Task.FromResult(plain);
    }

    public void Dispose() => _aead.Dispose();
}
