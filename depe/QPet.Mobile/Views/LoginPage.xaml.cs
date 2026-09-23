using System.Net.Sockets;
using QPet.Core;
using QPet.Mobile.Services;

namespace QPet.Mobile.Views;

/// <summary>
/// 登录/注册页(对齐桌面 LoginView):真实 WebSocket 认证,8 秒超时提示"无法连接服务器"。
/// 记住账号/密码(本机 Preferences)、离线登录走该账号本地缓存;被强制下线回本页并显示原因。
/// 服务器地址可编辑(默认 10.0.2.2 模拟器→宿主机,真机填电脑局域网 IP);打开自动检测:先 TCP 探活地址框值/10.0.2.2,
/// 不通再组播找局域网服务器(模拟器组播不通,靠 10.0.2.2 探活兜底),找到自动填入;登录成功记住。
/// </summary>
public partial class LoginPage : ContentPage
{
    private bool _isRegister;
    private bool _submitting;
    private bool _detecting; // 自动检测中:按钮防重入
    private bool _autoDetected; // 打开后只自动检测一次(OnAppearing 会多次触发:被踢回来/后台回来不重复)
    private int _detectInfoVersion; // 成功提示 3 秒自动隐藏;连续检测时旧提示不抢新提示的隐藏时机

    /// <summary>被踢原因暂存:AppShell 收到 Kicked 先记下再导航,本页 OnAppearing 显示一次。</summary>
    internal static string? PendingKickReason;

    public LoginPage() => InitializeComponent();

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // 已登录却回到本页(安卓返回键从主界面退回等):立即跳回主界面,登录页不做停留
        if (AppState.IsLoggedIn)
        {
            _ = Shell.Current.GoToAsync("main", animate: false);
            return;
        }

        // 预填:记住的账号/服务器地址(为空才填;MAUI Entry.Text 初值可空)
        if (string.IsNullOrEmpty(AccountEntry.Text))
            AccountEntry.Text = AppState.Settings.Read("Account") ?? "";
        if (string.IsNullOrEmpty(ServerEntry.Text))
            ServerEntry.Text = AppState.Settings.Read("ServerAddress") ?? DefaultAddress;
        // 密码在加密存储里(D2),读取是异步的:先让页面出来,密码到了再填(用户已开始输入就不覆盖)
        if (string.IsNullOrEmpty(PasswordEntry.Text))
            _ = PrefillPasswordAsync();

        // 被服务器强制下线:显示原因(展示一次后清)
        if (PendingKickReason is { Length: > 0 })
        {
            var reason = PendingKickReason;
            PendingKickReason = null;
            ShowError(reason);
        }

        // 打开自动检测一次:找到服务器直接填入,没找到静默(地址框保留记住的值)
        if (!_autoDetected)
        {
            _autoDetected = true;
            _ = AutoDetectAsync(quietOnFail: true);
        }
    }

    /// <summary>默认服务器地址:Android 模拟器访问宿主机固定 10.0.2.2(真机手动改电脑 IP)。</summary>
    internal static string DefaultAddress => $"10.0.2.2:{Protocol.DefaultPort}";

    async void OnSubmitClicked(object? sender, EventArgs e) => await SubmitAsync();

    // 本地离线登录:加载该账号的本地缓存直接进主界面(不连服务器、不校验密码;桌面同)
    async void OnOfflineClicked(object? sender, EventArgs e)
    {
        if (_submitting)
            return;
        var account = AccountEntry.Text.Trim();
        if (account.Length == 0)
        {
            ShowError("请输入账号(将使用该账号的本地缓存登录)");
            return;
        }
        var err = AppState.TryOfflineLogin(account); // 同步:缓存文件小,读盘毫秒级
        if (err is not null)
        {
            ShowError(err); // 无缓存 / 缓存损坏:提示先联网登录一次
            return;
        }
        HideError();
        await Shell.Current.GoToAsync("main", animate: false);
    }

    // 登录/注册模式互切:昵称输入 + 绿色欢迎横幅只出现在注册模式,离线入口隐藏避免歧义(桌面同)
    void OnToggleMode(object? sender, EventArgs e)
    {
        _isRegister = !_isRegister;

        TitleText.Text = _isRegister ? "注册新账号" : "Q版桌宠";
        SubtitleText.Text = _isRegister
            ? "创建账号 · 自动加入公共群聊 · 昵称会显示在群里"
            : "局域网聊天 · 好友 · 群聊";
        SubmitButton.Text = _isRegister ? "注册并登录" : "登 录";
        ToggleModeButton.Text = _isRegister ? "已有账号? 去登录" : "没有账号? 注册";
        OfflineButton.IsVisible = !_isRegister;
        ModeBanner.IsVisible = _isRegister;
        NicknamePanel.IsVisible = _isRegister;
        HideError();
    }

    // ---- 服务器自动检测 ----

    private void OnAutoDetect(object? sender, EventArgs e) => _ = AutoDetectAsync();

    /// <summary>
    /// 自动检测 QPet 服务器(打开自动触发 / 手动点按钮):先 TCP 探活地址框当前值与 10.0.2.2(模拟器宿主机),
    /// 都不通再发 UDP 组播找局域网服务器(AP 隔离/防火墙下组播不可用)。找到即填入地址框。
    /// quietOnFail:打开自动触发时失败不弹红字(保留记住的地址),手动点按钮才提示。
    /// </summary>
    private async Task AutoDetectAsync(bool quietOnFail = false)
    {
        if (_detecting)
            return;
        _detecting = true;
        AutoDetectButton.IsEnabled = false;
        AutoDetectButton.Text = "检测中...";
        HideError(); // 清上次登录/检测的残留提示(检测结果会另行给出,别让旧错误误导)
        HideDetectInfo(); // 旧成功提示同样作废
        try
        {
            var addr = await ProbeServerAsync(); // 返回裸 "ip:port",找不到为 null
            if (addr is not null)
            {
                // 找到服务器一定有可见反馈:地址不同则填入并说明;相同(确认当前地址可达)也提示,别让"没变化"误以为按钮没反应
                if (ServerEntry.Text.Trim() != addr)
                {
                    ServerEntry.Text = addr;
                    ShowDetectInfo($"已找到服务器 {addr},可直接登录");
                }
                else
                {
                    ShowDetectInfo($"服务器 {addr} 可达,可直接登录");
                }
            }
            else if (!quietOnFail)
                ShowError("未发现 QPet 服务器,请确认服务器已启动后重试,或手动输入地址");
        }
        catch
        {
            if (!quietOnFail)
                ShowError("自动检测失败,请手动输入服务器地址");
        }
        finally
        {
            _detecting = false;
            AutoDetectButton.IsEnabled = true;
            AutoDetectButton.Text = "自动检测";
        }
    }

    /// <summary>按顺序找可达服务器:地址框当前值 →(仅模拟器)10.0.2.2 探活 → 组播(2 秒,应答自报 ip:port)。</summary>
    private async Task<string?> ProbeServerAsync()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string> { ServerEntry.Text.Trim() };
        // 10.0.2.2 是 Android 模拟器访问宿主机的固定地址,真机上必然不通:只在虚拟设备上探,省一次 1.2 秒超时
        if (Microsoft.Maui.Devices.DeviceInfo.Current.DeviceType == Microsoft.Maui.Devices.DeviceType.Virtual)
            candidates.Add($"10.0.2.2:{Protocol.DefaultPort}");
        foreach (var raw in candidates)
        {
            if (raw.Length == 0 || !seen.Add(raw))
                continue;
            if (TrySplitHostPort(raw, out var host, out var port) && await CanReachTcpAsync(host, port))
                return $"{host}:{port}";
        }
        var found = await ServerDiscovery.FindAsync(); // 组播限时 2 秒收集回复
        return found.FirstOrDefault(a => a.Length > 0);
    }

    /// <summary>解析裸 "host:port" 或 "http(s)://host:port"(端口缺省用默认),解析失败返回 false。</summary>
    private static bool TrySplitHostPort(string raw, out string host, out int port)
    {
        host = "";
        port = Protocol.DefaultPort;
        var s = raw.Trim();
        var i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0)
            s = s[(i + 3)..];
        i = s.IndexOf(':');
        if (i >= 0)
        {
            host = s[..i];
            return host.Length > 0 && int.TryParse(s[(i + 1)..], out port) && port > 0;
        }
        host = s;
        return host.Length > 0;
    }

    /// <summary>TCP 连通探活(1.2 秒超时):端口能连上即认为服务器在跑。</summary>
    private static async Task<bool> CanReachTcpAsync(string host, int port)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.2));
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    async Task SubmitAsync()
    {
        if (_submitting)
            return;

        var account = AccountEntry.Text.Trim();
        var password = PasswordEntry.Text;
        if (account.Length == 0 || password.Length == 0)
        {
            ShowError("请输入账号和密码");
            return;
        }
        if (_isRegister)
        {
            var nickname = NicknameEntry.Text.Trim();
            if (nickname.Length == 0)
            {
                ShowError("请输入昵称");
                return;
            }
        }
        var url = ServerEntry.Text.Trim();
        if (url.Length == 0)
            url = DefaultAddress;

        string? err;
        _submitting = true;
        SubmitButton.IsEnabled = false;
        SubmitButton.Text = _isRegister ? "注册中..." : "登录中..."; // 请求期间按钮变加载文字,给用户反馈
        try
        {
            err = _isRegister
                ? await AppState.RegisterAsync(url, account, password, NicknameEntry.Text.Trim())
                : await AppState.LoginAsync(url, account, password);
        }
        finally
        {
            _submitting = false;
            SubmitButton.IsEnabled = true;
            SubmitButton.Text = _isRegister ? "注册并登录" : "登 录";
        }
        if (err is not null)
        {
            ShowError(err);
            return;
        }

        // 登录成功:记住服务器地址与账号;勾选记住密码才存,取消勾选清旧密码(下次不自动填)
        AppState.Settings.Write("ServerAddress", url);
        AppState.Settings.Write("Account", account);
        if (RememberSwitch.IsToggled)
        {
            // D2:密码进系统加密存储(安卓 Keystore)。存不了(个别机型 SecureStorage 不可用)
            // 就把开关拨回去 —— 别让用户以为记住了,下次却要重输;也不回落到明文
            if (!await SecurePassword.SetAsync(password))
                RememberSwitch.IsToggled = false;
        }
        else
        {
            SecurePassword.Remove();
        }
        HideError();
        await Shell.Current.GoToAsync("main", animate: false);
    }

    /// <summary>异步预填记住的密码(加密存储读取是异步的);用户已经开始输入就不覆盖。</summary>
    private async Task PrefillPasswordAsync()
    {
        var saved = await SecurePassword.GetAsync();
        if (saved.Length > 0 && string.IsNullOrEmpty(PasswordEntry.Text))
        {
            PasswordEntry.Text = saved;
            RememberSwitch.IsToggled = true; // 有记住的密码 = 开关回显为"记住"
        }
    }

    void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        HideDetectInfo(); // 成功/错误提示互斥,别让绿色成功与红色错误同时挂着
    }

    void HideError() => ErrorText.IsVisible = false;

    /// <summary>成功提示(绿字)显示 3 秒后自动消失;_detectInfoVersion 保证连续检测时只有最新的提示负责隐藏。</summary>
    void ShowDetectInfo(string text)
    {
        var v = ++_detectInfoVersion;
        DetectInfoText.Text = text;
        DetectInfoText.IsVisible = true;
        _ = AutoHideDetectInfoAsync(v);
    }

    void HideDetectInfo()
    {
        ++_detectInfoVersion;
        DetectInfoText.IsVisible = false;
    }

    async Task AutoHideDetectInfoAsync(int v)
    {
        await Task.Delay(3000);
        if (v == _detectInfoVersion)
            DetectInfoText.IsVisible = false;
    }
}
