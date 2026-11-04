// POSIX 路径小工具（对应 Go path.Dir / path.Join；远端路径永远是 POSIX 风格）。
namespace SubSsh.Plugin.Files;

internal static class PosixPath
{
    /// <summary>返回路径的目录部分（对应 Go path.Dir：无斜杠时 "."，根目录 "/"）。</summary>
    public static string Dir(string path)
    {
        var i = path.LastIndexOf('/');
        if (i < 0) return ".";
        if (i == 0) return "/";
        return path[..i];
    }

    /// <summary>拼接两个路径元素（对应 Go path.Join；第二段为空时返回第一段）。</summary>
    public static string Join(string a, string b)
    {
        if (b.Length == 0) return a;
        if (a.EndsWith('/')) return a + b;
        return a + "/" + b;
    }
}
