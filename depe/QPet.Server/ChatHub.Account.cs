using Microsoft.Data.Sqlite;
using QPet.Core;

namespace QPet.Server;

/// <summary>ChatHub 分部:账号与认证(注册/登录/昵称/宠物尺寸)+ 云端键值设置。</summary>
public partial class ChatHub
{
    // ---- 账号与认证 ----

    /// <summary>注册:账号唯一、密码非空、昵称 1-20 字;成功自动加入公共群聊。失败抛 ArgumentException。</summary>
    public UserInfo Register(string account, string password, string nickname)
    {
        account = account.Trim();
        password = password ?? "";
        nickname = nickname?.Trim() ?? "";
        if (account.Length < 2 || account.Length > 20)
            throw new ArgumentException("账号需为 2-20 位");
        if (password.Length < 4)
            throw new ArgumentException("密码至少 4 位");
        if (nickname.Length == 0 || nickname.Length > 20)
            throw new ArgumentException("昵称需为 1-20 字");

        using var conn = Open();
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM users WHERE Account = $a;";
        check.Parameters.AddWithValue("$a", account);
        if ((long)check.ExecuteScalar()! > 0)
            throw new ArgumentException("该账号已被注册");

        var userId = Guid.NewGuid().ToString("N");
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO users (UserId, Account, PasswordHash, PasswordPlain, Nickname, CreatedAtMs)
                VALUES ($id, $a, $p, $plain, $n, $t);
                """;
            cmd.Parameters.AddWithValue("$id", userId);
            cmd.Parameters.AddWithValue("$a", account);
            cmd.Parameters.AddWithValue("$p", HashPassword(password));
            cmd.Parameters.AddWithValue("$plain", password); // 明文供管理端显示;认证仍走哈希
            cmd.Parameters.AddWithValue("$n", nickname);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO group_members (GroupId, UserId) VALUES ($g, $u);";
            cmd.Parameters.AddWithValue("$g", PublicGroupId);
            cmd.Parameters.AddWithValue("$u", userId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        // 公共群新增成员:广播群更新,在线客户端的群成员数实时刷新;顺带一条欢迎系统消息
        SendSystem(1, PublicGroupId, $"欢迎「{nickname}」加入公共群聊");
        GroupUpdated?.Invoke(GetGroup(conn, PublicGroupId));

        return new UserInfo { UserId = userId, Nickname = nickname };
    }

    /// <summary>登录校验账号密码,成功返回用户档案。失败抛 ArgumentException。</summary>
    public UserInfo Authenticate(string account, string password)
    {
        account = account.Trim();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UserId, PasswordHash, Nickname, IsBanned FROM users WHERE Account = $a;";
        cmd.Parameters.AddWithValue("$a", account);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || !VerifyPassword(password ?? "", reader.GetString(1)))
            throw new ArgumentException("账号或密码错误");
        if (reader.GetInt64(3) > 0)
            throw new ArgumentException("账号已被禁用");
        return new UserInfo { UserId = reader.GetString(0), Nickname = reader.GetString(2) };
    }

    /// <summary>改昵称(1-20 字),成功触发 ProfileUpdated 广播给好友。</summary>
    public bool UpdateNickname(string userId, string nickname)
    {
        nickname = nickname?.Trim() ?? "";
        if (nickname.Length == 0 || nickname.Length > 20)
            return false;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET Nickname = $n WHERE UserId = $u;";
        cmd.Parameters.AddWithValue("$n", nickname);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.ExecuteNonQuery();

        lock (_lock)
        {
            if (_online.TryGetValue(userId, out var u))
                u.Nickname = nickname;
        }
        ProfileUpdated?.Invoke(userId, nickname);
        UsersChanged?.Invoke();
        return true;
    }

    /// <summary>取用户昵称(数据库权威)。</summary>
    public string GetNickname(string userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Nickname FROM users WHERE UserId = $u;";
        cmd.Parameters.AddWithValue("$u", userId);
        var result = cmd.ExecuteScalar();
        return result is null ? "" : (string)result;
    }

    /// <summary>保存宠物缩放尺寸(云端权威,登录/重连下发,其他端推送)。</summary>
    public void UpdatePetSize(string userId, double petSize)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET PetSize = $s WHERE UserId = $u;";
        cmd.Parameters.AddWithValue("$s", petSize);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>取云端宠物尺寸(登录下发用;查不到返回默认 200)。</summary>
    public double GetPetSize(string userId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PetSize FROM users WHERE UserId = $u;";
        cmd.Parameters.AddWithValue("$u", userId);
        var result = cmd.ExecuteScalar();
        return result is null ? PetConfig.DefaultPetSize : Convert.ToDouble(result);
    }

    // ---- 云端键值设置(群备注/未读游标/宠物参数等,按账号隔离,换设备登录即有) ----

    /// <summary>读某人全部键值设置(welcome 快照下发)。</summary>
    public Dictionary<string, string> GetUserSettings(string userId)
    {
        var map = new Dictionary<string, string>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM user_settings WHERE UserId = $u;";
        cmd.Parameters.AddWithValue("$u", userId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    /// <summary>写一条键值设置(UPSERT)。</summary>
    public void SetUserSetting(string userId, string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO user_settings (UserId, Key, Value) VALUES ($u, $k, $v)
            ON CONFLICT(UserId, Key) DO UPDATE SET Value = $v;
            """;
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
