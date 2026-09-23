using System.IO;
using System.Windows;
using System.Windows.Controls; // .NET 10 起 GridViewColumnHeader 从 Primitives 移到此处
using System.Windows.Input;
using System.Windows.Threading;
using QPet.Admin.Services;
using QPet.Core;
using QPet.Ui;
using QPet.Ui.Dialogs;

namespace QPet.Admin;

/// <summary>
/// 服务器管理窗口:连接地址框服务器(记住地址/本机默认,可点「自动检测」搜索) → 管理密码登录 → 账户列表(禁用/启用/删除/详情/群聊管理)。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AdminApiClient _api = new(); // 管理 REST 客户端(Bearer 认证 + 401 判定 + {error} 解析)
    private readonly JsonSettingsStore? _store; // 记住服务器地址(下次启动直接连)

    // 列表自动刷新(登录后每 5 秒拉一次:在线状态/新账号/禁用变化即时可见)
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _refreshing; // 上一次请求未完成时不重入

    // ---- 列头排序(默认按昵称升序;刷新后保持当前排序列与方向) ----
    private List<UserRow>? _rows;          // 当前列表(排序直接作用于它)
    private string _sortColumn = "昵称";
    private bool _sortAscending = true;
    private static readonly StringComparer ChineseComparer =
        StringComparer.Create(new System.Globalization.CultureInfo("zh-CN"), ignoreCase: true); // 中文按拼音排
    private static string AdminAccount => AppConstants.AdminAccount; // 特殊管理员账号:置顶且不可删除

    private sealed class UserRow : RowViewBase
    {
        // 注意:GridView 绑定只认属性,字段绑定会静默失败(列表显示空行)
        public string UserId { get; set; } = "";
        public string Account { get; set; } = "";
        public string Password { get; set; } = "";
        public bool IsBanned { get; set; }
        public long CreatedAtMs { get; set; }
        public string CreatedText =>
            DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public MainWindow()
    {
        InitializeComponent();
        // 启动直连:地址用上次/本机服务器(临时方案,不需要界面输入),
        // 密码有记住则自动填充,连接后自动登录
        _store = new JsonSettingsStore(Path.Combine(AppContext.BaseDirectory, "settings.json"));
        AdminPassBox.Password = _store.Read("AdminPassword") ?? "";
        ServerBox.Text = _store.Read("ServerAddress") ?? $"127.0.0.1:{Protocol.DefaultPort}"; // 地址框预填(默认本机)
        // 打开先自动检测:找到即连接该服务器;组播不可用/未发现则回退直连记住地址
        Loaded += async (_, _) =>
        {
            if (!await AutoDetectAsync(quietOnFail: true))
                Connect();
        };
        Closed += (_, _) => _refreshTimer.Stop();
        _refreshTimer.Tick += (_, _) => LoadUsers();
    }

    /// <summary>连接地址框里的服务器(自动检测填入或手动改;预填记住地址,默认本机)。</summary>
    private async void Connect()
    {
        _api.BaseUrl = ToHttpUrl(CurrentServerUrl());
        ConnStatusText.Text = "正在连接...";
        try
        {
            // 探活:带 token 请求 users;401 = 服务器通了但没登录,200 = token 仍有效
            using var r = await _api.GetAsync("users");
            if (r.Unauthorized)
            {
                ConnStatusText.Text = "服务器已连接";
                ConnStatusText.Foreground = UiPalette.Success;
                // 临时方案:记住的密码自动登录,没记住才让手输
                if (AdminPassBox.Password.Length > 0)
                    Login();
                else
                    AdminPassBox.Focus();
            }
            else if (r.IsSuccess)
            {
                ConnStatusText.Text = "已连接";
                ConnStatusText.Foreground = UiPalette.Success;
                MainPanel.IsEnabled = true; // token 仍有效,主体(刷新/列表/列头)直接可用
                LogoutButton.Visibility = Visibility.Visible; // 已登录才显示退出按钮
                LoadUsers(); // token 有效,直接进列表
            }
            else
            {
                ConnStatusText.Text = $"服务器返回错误 ({r.Status})";
                ConnStatusText.Foreground = UiPalette.Error;
            }
        }
        catch (Exception ex)
        {
            ConnStatusText.Text = "无法连接服务器: " + ex.Message;
            ConnStatusText.Foreground = UiPalette.Error;
        }
    }

    // ---- 登录 ----

    private void OnLoginKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Login();
    }

    private void OnLogin(object sender, RoutedEventArgs e) => Login();

    private async void Login()
    {
        // 同步地址框:自动检测填入或手动改地址后,直接点登录即连新服务器
        _api.BaseUrl = ToHttpUrl(CurrentServerUrl());

        var password = AdminPassBox.Password;
        if (password.Length == 0)
        {
            LoginErrorText.Text = "请输入管理密码";
            LoginErrorText.Visibility = Visibility.Visible;
            return;
        }

        LoginErrorText.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = false;
        LoginButton.Content = "登录中..."; // 请求期间按钮变加载态,给用户反馈
        try
        {
            var err = await _api.LoginAsync(password);
            if (err is not null)
            {
                LoginErrorText.Text = err;
                LoginErrorText.Visibility = Visibility.Visible;
                return;
            }
            ConnStatusText.Text = "已登录管理端";
            ConnStatusText.Foreground = UiPalette.Success;
            _store?.Write("ServerAddress", _api.BaseUrl.Replace("http://", "").Replace("https://", "")); // 记住地址
            _store?.Write("AdminPassword", password); // 临时方案:记住密码,下次启动自动登录
            CreateButton.IsEnabled = true; // 创建账户不需要选中用户,登录后即可用
            GroupManageButton.IsEnabled = true; // 群聊管理同
            MainPanel.IsEnabled = true; // 主体(刷新/列表/列头排序)登录后才可用
            LogoutButton.Visibility = Visibility.Visible; // 登录成功显示退出按钮
            LoadUsers();
            _refreshTimer.Start(); // 登录后列表自动刷新
        }
        catch (Exception ex)
        {
            LoginErrorText.Text = "连接失败: " + ex.Message;
            LoginErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "登录管理端";
        }
    }

    // ---- 自动检测服务器 ----

    private bool _detecting; // 自动检测中:按钮防重入

    /// <summary>地址框当前值:去空白,空则回退默认本机(与预填一致)。</summary>
    private string CurrentServerUrl()
    {
        var raw = ServerBox.Text.Trim();
        return raw.Length > 0 ? raw : $"127.0.0.1:{Protocol.DefaultPort}";
    }

    private void OnAutoDetect(object sender, RoutedEventArgs e) => _ = AutoDetectAsync();

    /// <summary>
    /// 自动检测局域网 QPet 服务器:向组播组发 QPET_DISCOVER 查询(2 秒),服务器(01-Server 已监听)单播回复地址。
    /// 找到:填入地址框 → 立即探活连接(密码已记住会顺带自动登录),返回 true;
    /// 没找到/失败:返回 false,状态栏提示手动输入(组播在 AP 隔离/防火墙下不可用)。
    /// quietOnFail:打开窗口自动触发时,未找到不刷红字提示(调用方会回退直连记住地址)。
    /// </summary>
    private async Task<bool> AutoDetectAsync(bool quietOnFail = false)
    {
        if (_detecting)
            return false;
        _detecting = true;
        AutoDetectButton.IsEnabled = false;
        AutoDetectButton.Content = "检测中...";
        LoginErrorText.Visibility = Visibility.Collapsed; // 清上次登录失败的残留提示(检测结果另行给出)
        ConnStatusText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty); // 回到默认棕
        ConnStatusText.Text = "正在搜索局域网中的 QPet 服务器...";
        try
        {
            var found = await ServerDiscovery.FindAsync();
            var addr = found.FirstOrDefault(a => a.Length > 0);
            if (addr is not null)
            {
                ServerBox.Text = addr; // 找到即填入,立即探活连接(Connect 读地址框)
                Connect();
                return true;
            }
            if (!quietOnFail)
            {
                ConnStatusText.Text = "未发现 QPet 服务器";
                ConnStatusText.Foreground = UiPalette.Error;
                LoginErrorText.Text = "未发现 QPet 服务器,请确认服务器已启动后重试,或手动输入地址";
                LoginErrorText.Visibility = Visibility.Visible;
            }
            return false;
        }
        catch
        {
            if (!quietOnFail)
            {
                ConnStatusText.Text = "自动检测失败(组播不可用),请手动输入地址";
                ConnStatusText.Foreground = UiPalette.Error;
            }
            return false;
        }
        finally
        {
            _detecting = false;
            AutoDetectButton.IsEnabled = true;
            AutoDetectButton.Content = "自动检测";
        }
    }

    /// <summary>退出登录:清 token 停刷新,主体回禁用(保留记住的密码,重登只需点登录)。</summary>
    private void OnLogout(object sender, RoutedEventArgs e)
    {
        _api.ResetToken();
        _refreshTimer.Stop();
        SetLoggedOut(); // 主体禁用 + 列表清空 + 空提示
        LogoutButton.Visibility = Visibility.Collapsed;
        LoginErrorText.Visibility = Visibility.Collapsed;
        ConnStatusText.Text = "已退出登录";
        ConnStatusText.Foreground = UiPalette.Success;
        AdminPassBox.Focus();
    }

    // ---- 用户列表 ----

    private async void LoadUsers()
    {
        if (_refreshing || !_api.HasToken)
            return;
        _refreshing = true;
        try
        {
            using var r = await _api.GetAsync("users");
            if (r.Unauthorized)
            {
                ConnStatusText.Text = "登录已过期,请重新输入管理密码";
                ConnStatusText.Foreground = UiPalette.Error;
                _api.ResetToken();
                _refreshTimer.Stop();
                SetLoggedOut(); // 清空旧列表并禁用主体,防止过期后误点旧数据
                return;
            }
            if (!r.IsSuccess)
            {
                ConnStatusText.Text = $"加载用户失败 ({r.Status})";
                ConnStatusText.Foreground = UiPalette.Error;
                return;
            }

            var users = new List<UserRow>();
            using var doc = r.Doc!;
            foreach (var item in doc.RootElement.GetProperty("users").EnumerateArray())
            {
                users.Add(new UserRow
                {
                    UserId = item.GetProperty("userId").GetString() ?? "",
                    Account = item.GetProperty("account").GetString() ?? "",
                    Password = item.GetProperty("password").GetString() ?? "",
                    Nickname = item.GetProperty("nickname").GetString() ?? "",
                    IsBanned = item.GetProperty("isBanned").GetBoolean(),
                    Online = item.GetProperty("online").GetBoolean(),
                    CreatedAtMs = item.GetProperty("createdAtMs").GetInt64(),
                });
            }
            // 刷新前记住选中(5 秒自动刷新/操作后刷新都不能打断多选)
            var selectedIds = UsersList.SelectedItems.Cast<UserRow>().Select(r => r.UserId).ToHashSet();
            _rows = users;
            SortRows();
            UpdateHeaderArrows();
            UsersList.ItemsSource = users;
            // 空列表提示:没账号时显示居中提示,避免空白列表让人误以为没登录成功
            EmptyHintText.Text = "暂无用户数据";
            EmptyHintText.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UserCountText.Text = $"共 {users.Count} 个账号";
            // 刷新后按 userId 恢复选中(选中的人还在列表里就保持选中)
            if (selectedIds.Count > 0)
            {
                foreach (var row in users)
                {
                    if (selectedIds.Contains(row.UserId))
                        UsersList.SelectedItems.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            ConnStatusText.Text = "加载失败: " + ex.Message;
            ConnStatusText.Foreground = UiPalette.Error;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadUsers();

    // ---- 列头排序 ----

    /// <summary>列头点击:同列反转方向,切换列按新列升序;排序在内存中做,不重新请求。</summary>
    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader { Role: GridViewColumnHeaderRole.Normal, Column.Header: string title })
            return;
        var column = StripArrow(title);
        if (column == _sortColumn)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }
        SortRows();
        UpdateHeaderArrows();
    }

    /// <summary>按当前排序列与方向排序(空昵称排最后,升降序都不影响它垫底)。</summary>
    private void SortRows()
    {
        if (_rows is not { Count: > 1 })
            return;
        _rows.Sort((a, b) =>
        {
            // 管理员账号(000000)置顶:任何列排序、任何方向都不影响它排最前
            var adminCmp = (a.Account == AdminAccount) == (b.Account == AdminAccount)
                ? 0
                : a.Account == AdminAccount ? -1 : 1;
            if (adminCmp != 0)
                return adminCmp;

            var c = _sortColumn switch
            {
                "账号" => ChineseComparer.Compare(a.Account, b.Account),
                "昵称" => CompareNickname(a, b),
                "注册时间" => a.CreatedAtMs.CompareTo(b.CreatedAtMs),
                "状态" => CompareStatus(a, b),
                _ => 0,
            };
            return _sortAscending ? c : -c;
        });
    }

    /// <summary>昵称排序:中文按拼音,空昵称垫底。</summary>
    private static int CompareNickname(UserRow a, UserRow b)
    {
        var an = a.Nickname.Trim();
        var bn = b.Nickname.Trim();
        if (an.Length == 0) return bn.Length == 0 ? 0 : 1; // 空昵称始终排最后
        if (bn.Length == 0) return -1;
        return ChineseComparer.Compare(an, bn);
    }

    /// <summary>状态排序:升序时未禁用在前、在线在前(禁用比在线状态更重)。</summary>
    private static int CompareStatus(UserRow a, UserRow b)
    {
        var banned = a.IsBanned.CompareTo(b.IsBanned);
        if (banned != 0) return banned;
        var online = a.Online.CompareTo(b.Online);
        return online != 0 ? -online : 0;
    }

    /// <summary>当前排序列的表头加 ▲/▼ 箭头,其余列恢复原始表头。</summary>
    private void UpdateHeaderArrows()
    {
        if (UsersList.View is not GridView grid)
            return;
        foreach (var col in grid.Columns)
        {
            if (col.Header is not string title)
                continue;
            var column = StripArrow(title);
            col.Header = column == _sortColumn ? $"{column} {(_sortAscending ? "▲" : "▼")}" : column;
        }
    }

    /// <summary>剥掉表头里可能带有的排序箭头,得到基础列名。</summary>
    private static string StripArrow(string title)
    {
        var i = title.LastIndexOf(' ');
        return i > 0 && (title.EndsWith('▲') || title.EndsWith('▼')) ? title[..i] : title;
    }

    /// <summary>当前选中的用户(Ctrl/Shift 可多选,批量操作遍历)。</summary>
    private List<UserRow> SelectedRows() => UsersList.SelectedItems.Cast<UserRow>().ToList();

    /// <summary>批量删除所选账号(在线账户会立即被踢下线)。</summary>
    private async void OnDeleteUser(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Count == 0)
            return;
        if (rows.Any(r => r.Account == AdminAccount)) // 双保险:含管理员账号直接拒绝(按钮已禁用,防其他路径)
            return;
        var names = string.Join(", ", rows.Select(r => $"{r.Account}({r.Nickname})"));
        if (new ConfirmDialog("删除确认",
                $"删除 {rows.Count} 个账号: {names}\n好友关系、群聊、消息将一并删除,不可恢复。") { Owner = this }.ShowDialog() != true)
            return;

        ConnStatusText.Text = $"正在删除 {rows.Count} 个账号...";
        var deleteResults = await BatchPostAsync(rows, "deleteUser");
        ReportBatch(deleteResults, "删除", "账号");
        LoadUsers();
    }

    /// <summary>批量禁用/启用所选账号(禁用时在线账户立即被踢下线)。</summary>
    private async void SetBanned(bool banned)
    {
        var rows = SelectedRows();
        if (rows.Count == 0)
            return;
        var action = banned ? "禁用" : "启用";
        var detail = banned ? "被禁用的账号将立即被踢下线且无法登录。" : "解禁后账号可正常登录。";
        if (new ConfirmDialog(action + "确认", $"{action} {rows.Count} 个账号\n{detail}") { Owner = this }.ShowDialog() != true)
            return;

        var endpoint = banned ? "ban" : "unban";
        ConnStatusText.Text = $"正在{action}...";
        var banResults = await BatchPostAsync(rows, endpoint);
        ReportBatch(banResults, action, "账号");
        LoadUsers(); // 无论成败都刷新(失败见状态栏)
    }

    /// <summary>汇总批量结果:全成显示成功数;有失败显示"成功 X 失败 Y + 首个失败原因",不被成功信息覆盖。</summary>
    private void ReportBatch(List<(UserRow Row, bool Ok, string Error)> results, string action, string unit)
    {
        var okCount = results.Count(r => r.Ok);
        var failCount = results.Count - okCount;
        if (failCount == 0)
        {
            ConnStatusText.Text = $"已{action} {okCount} 个{unit}";
            return;
        }
        var first = results.First(r => !r.Ok);
        ConnStatusText.Text = $"{action}成功 {okCount} 个,失败 {failCount} 个({first.Row.Account}: {first.Error})";
    }

    /// <summary>对选中账号串行调用管理端点;返回每行的结果(单行失败不中断后续)。</summary>
    private async Task<List<(UserRow Row, bool Ok, string Error)>> BatchPostAsync(List<UserRow> rows, string endpoint)
    {
        var results = new List<(UserRow, bool, string)>();
        foreach (var row in rows)
        {
            try
            {
                using var r = await _api.PostAsync(endpoint, new { userId = row.UserId });
                if (r.Unauthorized)
                {
                    ConnStatusText.Text = "登录已过期,请重新登录";
                    _api.ResetToken();
                    _refreshTimer.Stop();
                    SetLoggedOut(); // 清空旧列表并禁用主体,防止过期后误点旧数据
                    results.Add((row, false, "登录过期"));
                    return results;
                }
                if (!r.IsSuccess)
                {
                    results.Add((row, false, $"({r.Status}){r.Error}"));
                    continue;
                }
                results.Add((row, true, ""));
            }
            catch (Exception ex)
            {
                results.Add((row, false, ex.Message));
            }
        }
        return results;
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var rows = SelectedRows();
        BanButton.IsEnabled = rows.Count > 0 && rows.All(r => !r.IsBanned);
        UnbanButton.IsEnabled = rows.Count > 0 && rows.All(r => r.IsBanned);
        // 管理员账号不可删除:选中含 000000 时删除按钮禁用
        DeleteButton.IsEnabled = rows.Count > 0 && rows.All(r => r.Account != AdminAccount);
        DetailButton.IsEnabled = rows.Count == 1; // 账号详情逐个操作(双击账号行同效)
    }

    /// <summary>双击账号行:进入账号详情页。</summary>
    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (UsersList.SelectedItem is UserRow)
            OnDetails(this, new RoutedEventArgs());
    }

    /// <summary>账号详情页:昵称/账号/密码(可改)/好友/群聊(操作实时推给在线客户端)。</summary>
    private void OnDetails(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Count != 1)
            return;
        var row = rows[0];
        new AccountDetailDialog(_api, row.UserId,
            row.Account, row.Nickname, row.Password, row.IsBanned, row.Online)
        {
            Owner = this,
        }.ShowDialog();
        LoadUsers(); // 关系/密码可能已变,回来自动刷新列表
    }

    // ---- 操作 ----

    /// <summary>群聊管理:全部群列表,查看成员/移出/添加成员/解散群(变更实时推给在线客户端)。</summary>
    private void OnGroupManage(object sender, RoutedEventArgs e)
    {
        new GroupManageDialog(_api)
        {
            Owner = this,
        }.ShowDialog();
    }

    /// <summary>创建账户:弹窗填账号/密码/昵称,调管理 API 创建(服务器做唯一性/长度校验)。</summary>
    private async void OnCreateUser(object sender, RoutedEventArgs e)
    {
        var dlg = new CreateUserDialog { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            using var r = await _api.PostAsync("createUser",
                new { account = dlg.Account, password = dlg.Password, nickname = dlg.Nickname });
            if (!r.IsSuccess)
            {
                new ConfirmDialog("提示", $"创建失败: {r.Error}", showCancel: false) { Owner = this }.ShowDialog();
                return;
            }
            ConnStatusText.Text = $"已创建账户 {dlg.Account}";
            LoadUsers();
        }
        catch (Exception ex)
        {
            ConnStatusText.Text = "创建失败: " + ex.Message;
        }
    }

    private void OnBan(object sender, RoutedEventArgs e) => SetBanned(true);

    private void OnUnban(object sender, RoutedEventArgs e) => SetBanned(false);

    // ---- 内部 ----

    /// <summary>未登录/登录过期:主体整体禁用(刷新/列表/列头不可点),并清空旧列表防误点过期数据。</summary>
    private void SetLoggedOut()
    {
        MainPanel.IsEnabled = false;
        _rows = null;
        UsersList.ItemsSource = null;
        UserCountText.Text = "";
        EmptyHintText.Text = "登录管理端后显示用户列表";
        EmptyHintText.Visibility = Visibility.Visible;
    }

    /// <summary>归一化服务器地址为 http://host:port(不带路径)。</summary>
    private static string ToHttpUrl(string raw)
    {
        var url = raw.Split(',')[0].Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        var uri = new Uri(url);
        var scheme = uri.Scheme == "https" ? "https" : "http";
        var port = uri.IsDefaultPort ? Protocol.DefaultPort : uri.Port;
        return $"{scheme}://{uri.Host}:{port}";
    }
}
