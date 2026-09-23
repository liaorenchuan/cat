using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>ChatHub 分部:内部(连接/好友判定/溢出裁剪/在线清扫/密码学)。</summary>
public partial class ChatHub
{
    // ---- 内部 ----

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        // 初始化连接级 PRAGMA:WAL 读写不互斥 + busy_timeout 等锁而非立刻抛 SQLITE_BUSY
        // (多线程同时写聊天/好友/群时高频触发;执行失败不致命,跳过)
        try
        {
            using var init = conn.CreateCommand();
            init.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            init.ExecuteNonQuery();
        }
        catch { }
        return conn;
    }

    /// <summary>两人是否好友(SyncServer 路由 profileUpdated 用)。</summary>
    public bool IsFriend(string a, string b)
    {
        using var conn = Open();
        return AreFriends(conn, a, b);
    }

    private static bool AreFriends(SqliteConnection conn, string a, string b)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM friends WHERE (UserA = $x AND UserB = $y) OR (UserA = $y AND UserB = $x);";
        cmd.Parameters.AddWithValue("$x", a);
        cmd.Parameters.AddWithValue("$y", b);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static bool HasPendingRequest(SqliteConnection conn, string from, string to)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM friend_requests WHERE FromUserId = $f AND ToUserId = $t AND Status = 0;";
        cmd.Parameters.AddWithValue("$f", from);
        cmd.Parameters.AddWithValue("$t", to);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>消息超上限删最旧。</summary>
    private void TrimIfOverflow()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM messages WHERE Seq <= (
                SELECT Seq FROM messages ORDER BY Seq DESC LIMIT 1 OFFSET $keep);
            """;
        cmd.Parameters.AddWithValue("$keep", MaxMessages);
        cmd.ExecuteNonQuery();
    }

    /// <summary>惰性清理在线表:3 秒最多一次(与在线超时同量级,保证超时判定及时推送)。</summary>
    private void SweepIfDue()
    {
        List<string>? removed = null;
        lock (_lock)
        {
            if ((DateTime.UtcNow - _lastSweep).TotalSeconds < 3)
                return;
            _lastSweep = DateTime.UtcNow;

            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - UserTtlMs;
            var expired = _online.Where(kv => kv.Value.LastSeenMs < cutoff).Select(kv => kv.Key).ToList();
            foreach (var id in expired)
                _online.Remove(id);
            if (expired.Count > 0)
                removed = expired;
        }
        if (removed is not null)
            UsersChanged?.Invoke();
    }

    private static string MinId(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a : b;
    private static string MaxId(string a, string b) => string.CompareOrdinal(a, b) > 0 ? a : b;

    private static string HashPassword(string password)
    {
        var salt = Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(salt + password));
        return $"{salt}:{Convert.ToHexString(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2)
            return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(parts[0] + password));
        return Convert.ToHexString(hash).Equals(parts[1], StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>管理视图的用户档案(管理 REST 输出)。</summary>
public sealed record AdminUser(string UserId, string Account, string Nickname, bool IsBanned, bool Online, long CreatedAtMs, string Password);
