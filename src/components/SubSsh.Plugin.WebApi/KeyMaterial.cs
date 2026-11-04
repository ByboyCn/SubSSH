// ed25519 密钥生成 / OpenSSH 私钥解析与导出（BouncyCastle 实现）。
// 说明：当前支持无口令（cipher=none）的 OpenSSH 私钥容器；带口令的 bcrypt kdf 解析为后续增强项。
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Security;

namespace SubSsh.Plugin.WebApi;

public static class KeyMaterial
{
    private const string Magic = "openssh-key-v1\0";
    private const string KeyType = "ssh-ed25519";

    /// <summary>生成新 ed25519 密钥对，返回（OpenSSH 私钥 PEM，authorized_keys 公钥行）。</summary>
    public static (string PrivatePem, string PublicKeyLine) Generate(string comment)
    {
        var gen = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
        gen.Init(new Org.BouncyCastle.Crypto.Parameters.Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        var priv = ((Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters)pair.Private).GetEncoded();
        var pub = ((Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        var publicKeyLine = $"{KeyType} {ToBase64(StringBytes(KeyType).Concat(StringBytes(pub)).ToArray())} {comment}".TrimEnd();
        return (Armored(PrivateSection(pub, priv, comment), pub), publicKeyLine);
    }

    /// <summary>
    /// 解析 OpenSSH 私钥 PEM（ed25519，cipher=none），返回（原始私钥 32 字节，authorized_keys 公钥行，注释）。
    /// </summary>
    public static (byte[] RawPrivate, string PublicKeyLine, string Comment) Import(string pem)
    {
        var b64 = new StringBuilder();
        foreach (var line in pem.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('-') || t.StartsWith('#')) continue;
            b64.Append(t);
        }
        var data = Convert.FromBase64String(b64.ToString());
        if (data.Length <= Magic.Length || Encoding.ASCII.GetString(data, 0, Magic.Length) != Magic)
            throw new SubSsh.Core.Abstractions.SubSshException("仅支持 OpenSSH 格式私钥（BEGIN OPENSSH PRIVATE KEY）");

        var r = new Reader(data, Magic.Length);
        var cipher = r.ReadString();
        var kdf = r.ReadString();
        _ = r.ReadStringBytes(); // kdfoptions
        var keyCount = r.ReadUInt32();
        if (cipher != "none" || kdf != "none")
            throw new SubSsh.Core.Abstractions.SubSshException("暂不支持口令保护的私钥，请先去除口令后再导入");
        if (keyCount != 1)
            throw new SubSsh.Core.Abstractions.SubSshException("私钥容器内密钥数量异常");

        var pubBlob = r.ReadStringBytes();
        var privBlob = r.ReadStringBytes();

        var pr = new Reader(privBlob, 0);
        var check1 = pr.ReadUInt32();
        var check2 = pr.ReadUInt32();
        if (check1 != check2)
            throw new SubSsh.Core.Abstractions.SubSshException("私钥校验位不匹配（文件损坏或口令错误）");
        var keyType = pr.ReadString();
        if (keyType != KeyType)
            throw new SubSsh.Core.Abstractions.SubSshException($"暂不支持密钥类型: {keyType}（仅支持 ed25519）");
        var pub = pr.ReadStringBytes();
        var priv64 = pr.ReadStringBytes();
        if (priv64.Length != 64)
            throw new SubSsh.Core.Abstractions.SubSshException("私钥长度异常");
        var comment = pr.ReadString();
        var rawPrivate = priv64[32..];

        var publicKeyLine = $"{KeyType} {ToBase64(pubBlob.Length > 0 ? pubBlob : StringBytes(KeyType).Concat(StringBytes(pub)).ToArray())} {comment}".TrimEnd();
        return (rawPrivate, publicKeyLine, comment);
    }

    // ---------- 内部编码 ----------

    private static byte[] PrivateSection(byte[] pub, byte[] priv32, string comment)
    {
        // full64 = 私 32 + 公 32（OpenSSH 容器约定）
        var full64 = new byte[64];
        priv32.CopyTo(full64, 0);
        pub.CopyTo(full64, 32);

        Span<byte> check = stackalloc byte[4];
        RandomNumberGenerator.Fill(check);
        var checkVal = BinaryPrimitives.ReadUInt32BigEndian(check);

        using var inner = new MemoryStream();
        WriteString(inner, StringBytes(KeyType));
        WriteString(inner, StringBytes(pub));
        WriteBytes(inner, full64);
        WriteString(inner, Encoding.UTF8.GetBytes(comment));
        byte pad = 1;
        while ((inner.Length + 4) % 8 != 0) inner.WriteByte(pad++);

        using var body = new MemoryStream();
        WriteUInt32(body, checkVal);
        WriteUInt32(body, checkVal);
        WriteUInt32(body, (uint)inner.Length);
        inner.Position = 0;
        inner.CopyTo(body);
        return body.ToArray();
    }

    private static string Armored(byte[] privSection, byte[] pub)
    {
        using var ms = new MemoryStream();
        WriteBytes(ms, Encoding.ASCII.GetBytes(Magic));
        WriteString(ms, Encoding.ASCII.GetBytes("none"));   // cipher
        WriteString(ms, Encoding.ASCII.GetBytes("none"));   // kdf
        WriteUInt32(ms, 0);                                  // kdfoptions 空串
        WriteUInt32(ms, 1);                                  // numkeys
        WriteString(ms, StringBytes(KeyType).Concat(StringBytes(pub)).ToArray()); // 公钥 blob
        WriteString(ms, privSection);
        var b64 = Convert.ToBase64String(ms.ToArray());
        var lines = Enumerable.Range(0, (b64.Length + 69) / 70)
            .Select(i => b64.Substring(i * 70, Math.Min(70, b64.Length - i * 70)));
        return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + string.Join('\n', lines) + "\n-----END OPENSSH PRIVATE KEY-----\n";
    }

    private static byte[] StringBytes(ReadOnlySpan<byte> payload)
    {
        var b = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)payload.Length);
        payload.CopyTo(b.AsSpan(4));
        return b;
    }

    private static string ToBase64(byte[] b) => Convert.ToBase64String(b);

    private static byte[] StringBytes(string s) => StringBytes(Encoding.UTF8.GetBytes(s));

    private static void WriteString(Stream s, byte[] payload) => WriteBytes(s, StringBytes(payload));
    private static void WriteBytes(Stream s, byte[] b) => s.Write(b, 0, b.Length);
    private static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private sealed class Reader(byte[] data, int pos)
    {
        public uint ReadUInt32()
        {
            var v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            pos += 4;
            return v;
        }
        public byte[] ReadStringBytes()
        {
            var len = (int)ReadUInt32();
            if (len < 0 || pos + len > data.Length)
                throw new SubSsh.Core.Abstractions.SubSshException("私钥容器结构损坏");
            var b = data[pos..(pos + len)];
            pos += len;
            return b;
        }
        public string ReadString() => Encoding.UTF8.GetString(ReadStringBytes());
    }
}
