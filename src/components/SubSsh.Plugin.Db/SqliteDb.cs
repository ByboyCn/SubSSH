// SubSsh.Plugin.Db — SQLite 实现（对应 Go internal/store/store.go）。
// GPL-3.0，移植自 SubSSH；migration SQL 原样嵌入执行（仅按 Microsoft.Data.Sqlite 约束拆分多语句）。
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SubSsh.Core.Abstractions;

namespace SubSsh.Plugin.Db;

/// <summary>SQLite 实现。可插拔语义由 DbPlugin 的 ctx.Track 清理效果保证（卸载即关闭连接）。</summary>
public sealed partial class SqliteDb : ISubSshStoreFull
{
    private readonly SqliteConnection _conn;
    // Microsoft.Data.Sqlite 的单连接不是线程安全的；Go 版的 writeMu 热点写锁在此推广为全库串行锁。
    private readonly object _sync = new();

    public string ConnectionString => _conn.ConnectionString;

    private SqliteDb(SqliteConnection conn) => _conn = conn;

    public static SqliteDb Open(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        // 旧数据文件名迁移：subssh.db 不存在而 onessh.db 存在时原地改名，保留全部数据
        var legacy = Path.Combine(dataDir, "onessh.db");
        var current = Path.Combine(dataDir, "subssh.db");
        if (File.Exists(legacy) && !File.Exists(current))
        {
            try { File.Move(legacy, current); }
            catch (IOException) { /* 同名占用等场景忽略，按新建库继续 */ }
        }
        // Go 版为 artifacts / command-runs 数据目录做 0700；Windows 上 ACL 语义不同，尽力而为。
        foreach (var name in new[] { "artifacts", "command-runs" })
        {
            var dir = Directory.CreateDirectory(Path.Combine(dataDir, name));
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (PlatformNotSupportedException) { /* TODO(port): 旧内核/受限 FS 下忽略 */ }
            }
        }
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDir, "subssh.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        conn.Open();
        var db = new SqliteDb(conn);
        db.ConfigurePragmas();
        db.Migrate();
        return db;
    }

    // Go DSN: _pragma busy_timeout(5000), _txlock immediate, _pragma foreign_keys(ON)，外加 journal_mode=WAL。
    // Microsoft.Data.Sqlite 不支持 DSN pragma，改用连接级 PRAGMA；_txlock=immediate 在单连接串行模型下无意义。
    private void ConfigurePragmas()
    {
        lock (_sync)
        {
            Exec("PRAGMA busy_timeout=5000");
            Exec("PRAGMA foreign_keys=ON");
            Exec("PRAGMA journal_mode=WAL");
        }
    }

    private void Migrate()
    {
        lock (_sync)
        {
            Exec(@"CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at INTEGER NOT NULL
            )");
            var asm = Assembly.GetExecutingAssembly();
            foreach (var m in LoadMigrations(asm))
            {
                ApplyMigration(m.Version, m.Sql);
            }
        }
    }

    private static List<(int Version, string Sql)> LoadMigrations(Assembly asm)
    {
        var list = new List<(int, string)>();
        foreach (var name in asm.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var version = ParseMigrationVersion(name);
            list.Add((version, reader.ReadToEnd()));
        }
        return list;
    }

    private static int ParseMigrationVersion(string resourceName)
    {
        // 资源名形如 SubSsh.Plugin.Db.Migrations.0001_init.sql
        var dot = resourceName.LastIndexOf(".sql", StringComparison.OrdinalIgnoreCase);
        var start = resourceName.LastIndexOf('.', dot - 1) + 1;
        var underscore = resourceName.IndexOf('_', start);
        return int.Parse(resourceName[start..(underscore < 0 ? dot : underscore)]);
    }

    private void ApplyMigration(int version, string sql)
    {
        lock (_sync)
        {
            using var tx = _conn.BeginTransaction();
            var applied = ScalarLong("SELECT count(*) FROM schema_migrations WHERE version=?", tx, version);
            if (applied != 0)
            {
                tx.Commit();
                return;
            }
            switch (version)
            {
                case 2:
                    if (!TableHasColumn(tx, "tokens", "manage_hosts"))
                    {
                        ExecMulti(sql, tx);
                    }
                    break;
                case 8:
                    if (!TableHasColumn(tx, "audit", "token_name"))
                    {
                        Exec(tx, "ALTER TABLE audit ADD COLUMN token_name TEXT");
                    }
                    ExecMulti(sql, tx);
                    break;
                case 11:
                    if (!TableHasColumn(tx, "jobs", "log_bytes"))
                    {
                        Exec(tx, "ALTER TABLE jobs ADD COLUMN log_bytes INTEGER NOT NULL DEFAULT 0");
                    }
                    ExecMulti(sql, tx);
                    if (!TableHasColumn(tx, "command_runs", "output_cleaned"))
                    {
                        Exec(tx, "ALTER TABLE command_runs ADD COLUMN output_cleaned INTEGER NOT NULL DEFAULT 0");
                    }
                    break;
                case 12:
                    if (!TableHasColumn(tx, "audit", "command_run_ids_json"))
                    {
                        ExecMulti(sql, tx);
                    }
                    break;
                default:
                    ExecMulti(sql, tx);
                    break;
            }
            Exec(tx, "INSERT INTO schema_migrations(version,applied_at) VALUES(?,strftime('%s','now'))", version);
            tx.Commit();
        }
    }

    private static bool TableHasColumn(SqliteTransaction tx, string table, string column)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA table_info(" + table + ")";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            if (rd.GetString(1) == column)
            {
                return true;
            }
        }
        return false;
    }

    public Task<bool> HealthAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            Exec("SELECT 1");
        }
        return Task.FromResult(true);
    }

    public Task<(bool Busy, long Log, long Checkpointed)> CheckpointWalAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var rd = cmd.ExecuteReader();
            rd.Read();
            return Task.FromResult((rd.GetBoolean(0), rd.GetInt64(1), rd.GetInt64(2)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync();
    }

    // ---------- 底层执行辅助（全部要求调用方已持有 _sync 锁） ----------

    private void Exec(string sql, params object?[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteTransaction tx, string sql, params object?[] args)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, args);
        cmd.ExecuteNonQuery();
    }

    /// <summary>执行恰好影响一行的写入，没有命中时抛 SubSshException（对应 Go execOne 的 sql.ErrNoRows）。</summary>
    private static void ExecOne(SqliteTransaction tx, string sql, params object?[] args)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, args);
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new SubSshException("记录不存在");
        }
    }

    private static long ScalarLong(string sql, SqliteTransaction tx, params object?[] args)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, args);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private long ScalarLong(string sql, params object?[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, args);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Microsoft.Data.Sqlite 的 '?' 占位符与命名参数互不相认（'?' 会被当作待赋值的匿名参数）。
    /// 调用点均先设置 cmd.CommandText 再调用本方法；这里做引号/注释感知的 '?' → :pN 展开，
    /// 并按顺序绑定命名参数（'' 转义、-- 行注释、/* */ 块注释均正确跳过）。
    /// </summary>
    private static void Bind(SqliteCommand cmd, object?[] args)
    {
        if (args.Length == 0) return;
        var sql = cmd.CommandText;
        var sb = new System.Text.StringBuilder(sql.Length + args.Length * 5);
        var pi = 0;
        var inSingle = false; var inDouble = false; var inLine = false; var inBlock = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (inLine) { if (c == '\n') inLine = false; sb.Append(c); continue; }
            if (inBlock) { if (c == '*' && next == '/') { inBlock = false; sb.Append("*/"); i++; } else sb.Append(c); continue; }
            if (!inSingle && !inDouble && c == '-' && next == '-') { inLine = true; sb.Append("--"); i++; continue; }
            if (!inSingle && !inDouble && c == '/' && next == '*') { inBlock = true; sb.Append("/*"); i++; continue; }
            if (inSingle) { if (c == '\'') { if (next == '\'') { sb.Append("''"); i++; continue; } inSingle = false; } sb.Append(c); continue; }
            if (inDouble) { if (c == '"') inDouble = false; sb.Append(c); continue; }
            if (c == '\'') { inSingle = true; sb.Append(c); continue; }
            if (c == '"') { inDouble = true; sb.Append(c); continue; }
            if (c == '?' && next != '?')
            {
                sb.Append(":p").Append(pi);
                cmd.Parameters.Add(new SqliteParameter($":p{pi}", args[pi] ?? DBNull.Value));
                pi++;
                continue;
            }
            sb.Append(c);
        }
        cmd.CommandText = sb.ToString();
    }

    private static string Placeholders(int n) => string.Join(",", Enumerable.Repeat("?", n));

    // ---------- 行读取辅助 ----------

    private static long? NL(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetInt64(i);
    private static long IL(SqliteDataReader rd, int i) => rd.GetInt64(i);
    private static string? ST(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
    private static string SN(SqliteDataReader rd, int i) => rd.GetString(i);
    private static double? DB(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetDouble(i);
    private static byte[]? BL(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : (byte[])rd.GetValue(i);
    private static bool BO(SqliteDataReader rd, int i) => rd.GetInt64(i) != 0;
    private static int II(SqliteDataReader rd, int i) => (int)rd.GetInt64(i);

    // ---------- 通用工具（对应 Go store 包级函数） ----------

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>审计时间戳：Go NowAudit() 返回 UnixMilli。</summary>
    public static long NowAudit() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal static string TokenHash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    internal static string MustJson(object? value)
    {
        try { return JsonSerializer.Serialize(value); }
        catch { return "[]"; }
    }

    internal static string EscapeLike(string value)
        => value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    // ---------- disabled_tools 编解码（Go encodeDisabledTools/decodeDisabledTools） ----------

    /// <summary>
    /// 持久化边界校验 hook（对应 Go toolgroups.NormalizeList）。
    /// TODO(port): 待 Plugin.ToolGroups 提供分组常量后由并行任务接入；
    /// 当前默认实现只做 null 归一为空数组，不做未知分组名拒绝（fail-open 风险见 Go 版注释）。
    /// </summary>
    public static Func<string[]?, string[]?>? NormalizeDisabledToolsHook { get; set; }

    internal static (string Json, string[] Normalized) EncodeDisabledTools(IReadOnlyList<string>? names)
    {
        var array = names as string[] ?? names?.ToArray() ?? [];
        var normalized = NormalizeDisabledToolsHook != null
            ? NormalizeDisabledToolsHook(array) ?? []
            : array;
        return (JsonSerializer.Serialize(normalized), normalized);
    }

    internal static string[] DecodeDisabledTools(string raw)
    {
        if (raw.Length == 0)
        {
            return [];
        }
        var names = JsonSerializer.Deserialize<string[]>(raw);
        // JSON null 不能当成空 denylist fail-open，否则 MCP 会放开全部工具组（与 Go 版一致）。
        return names ?? throw new SubSshException("disabled_tools_json must be a JSON array");
    }

    // ---------- migration SQL 拆分（Microsoft.Data.Sqlite 单命令只支持一条语句） ----------

    /// <summary>
    /// 按 ';' 拆分 SQL 语句，正确跳过 '--' 行注释、'/* */' 块注释、'…' 字符串，
    /// 并识别 CREATE TRIGGER 的 BEGIN…END（'END;' 不是语句结尾）。
    /// </summary>
    internal static IReadOnlyList<string> SplitSqlStatements(string sql)
    {
        var statements = new List<string>();
        var sb = new StringBuilder(sql.Length);
        var depth = 0; // BEGIN…END 深度
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'' )
            {
                var end = i + 1;
                while (end < sql.Length)
                {
                    if (sql[end] == '\'')
                    {
                        if (end + 1 < sql.Length && sql[end + 1] == '\'') { end += 2; continue; }
                        break;
                    }
                    end++;
                }
                sb.Append(sql, i, Math.Min(end + 1, sql.Length) - i);
                i = end + 1;
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') { i++; }
                sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var end = i;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                var word = sql[i..end];
                if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)) depth++;
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase) && depth > 0) depth--;
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == ';')
            {
                if (depth == 0)
                {
                    var text = sb.ToString().Trim();
                    sb.Clear();
                    if (text.Length > 0) statements.Add(text);
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        var tail = sb.ToString().Trim();
        if (tail.Length > 0) statements.Add(tail);
        return statements;
    }

    /// <summary>逐条执行（Microsoft.Data.Sqlite 单命令单语句约束）。</summary>
    private void ExecMulti(string sql, SqliteTransaction? tx = null)
    {
        foreach (var statement in SplitSqlStatements(sql))
        {
            if (tx != null)
            {
                Exec(tx, statement);
            }
            else
            {
                Exec(statement);
            }
        }
    }
}
