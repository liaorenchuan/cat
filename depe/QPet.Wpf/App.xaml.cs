using System.Globalization;
using System.IO;
using System.Windows;
using QPet.Core;
using QPet.Wpf.Services;
using QPet.Wpf.Views;
using QPet.Wpf.Windows;

namespace QPet.Wpf;

/// <summary>
/// 应用入口。单窗口(MainWindow 壳)内切换 登录 / 聊天 两个视图(聊天界面含
/// 消息/好友/群聊/宠物小屋四个页签);「外出玩耍」唤起悬浮宠物(PetFloatingWindow),「回家」切回宠物小屋。
/// 无托盘:关闭主窗口 = 退出整个应用(ShutdownMode.OnMainWindowClose,悬浮窗同步关闭)。
/// 记住登录:账号/服务器地址存 settings.json,密码经 DPAPI 加密存储,启动回登录页自动预填。
/// 本程序是纯客户端:聊天/好友/数据全部在独立的 QPet.Server 上,宠物逻辑纯本地。
/// </summary>
public partial class App : Application
{
    private MainWindow? _shell;               // 唯一主窗口(壳)
    private PetFloatingWindow? _floatingWindow; // 悬浮宠物(外出玩耍形态,常驻单例)
    private JsonSettingsStore? _settingsStore;
    private LocalCacheStore? _cacheStore;
    private LocalCache? _currentCache;        // 当前会话缓存(离线模式 ChatView 读消息用)
    private bool _cacheSavedThisSession;      // 本会话已落盘(首次 HistoryLoaded 触发一次)
    private string _myAccount = "";

    /// <summary>崩溃日志路径(全局异常捕获,排查闪退用)。</summary>
    private static readonly string CrashLog =
        Path.Combine(AppContext.BaseDirectory, "crash.log");

    /// <summary>设置存储(exe 同目录 settings.json),视图与凭据共用。</summary>
    internal JsonSettingsStore? SettingsStore => _settingsStore;

    /// <summary>局域网客户端:连接独立服务器(聊天/好友/群聊),事件已切回 UI 线程。</summary>
    internal static SyncClient SyncClient { get; } =
        new(a => Current?.Dispatcher.BeginInvoke(a));

    /// <summary>当前登录账号(聊天界面显示,登录前为空)。</summary>
    internal string MyAccount => _myAccount;

    /// <summary>当前会话缓存(离线登录时 ChatView 灌消息用;在线时为 null)。</summary>
    internal LocalCache? CurrentCache => _currentCache;

    /// <summary>当前登录用户 ID(登录前为空)。</summary>
    internal string MyUserId => SyncClient.MyUser.UserId;

    /// <summary>当前登录用户昵称(登录前为空)。</summary>
    internal string MyNickname => SyncClient.MyUser.Nickname;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常捕获:UI 线程回调异常(Dispatcher.BeginInvoke)默认直接崩进程,
        // 这里把堆栈写到 crash.log 便于定位(如群搜索闪退)
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            args.Handled = true; // 不弹崩溃框,写日志后忽略(避免再次崩溃)
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash(args.ExceptionObject as Exception);

        // 加载用户配置(exe 同目录 settings.json)
        _settingsStore = new JsonSettingsStore(Path.Combine(AppContext.BaseDirectory, "settings.json"));
        PetConfig.Load(_settingsStore);
        // 离线缓存(exe 同目录 cache-账号.json);首次全量历史拉完落盘一次,登出/退出再落盘
        _cacheStore = new LocalCacheStore(AppContext.BaseDirectory);
        SyncClient.HistoryLoaded += OnHistoryLoadedSaveCache;

        // 唯一主窗口:关闭即退出(默认 ShutdownMode.OnMainWindowClose)
        _shell = new MainWindow(this);
        MainWindow = _shell;
        _shell.Show();

        // 启动固定显示登录页:即使上次登录记住过账号,也不静默跳过登录(每次启动都要手动登录)
        NavigateTo(AppView.Login);

        // 云端宠物尺寸:登录下发 / 同账号其他端修改推送,统一应用并持久化
        SyncClient.PetSizeReceived += OnCloudPetSize;
        // 云端其余宠物参数(WalkSpeed/EdgeMargin/Topmost):welcome 下发 + 其他端修改回推
        SyncClient.WelcomeReceived += OnWelcomeCloudParams;
        SyncClient.SettingReceived += OnCloudSetting;
        // 被管理员禁用/删除:长期订阅踢人信号,强制下线回登录页
        // (AuthFailed 登录流程中由 LoginWithAsync 临时订阅处理,此处只在已登录态生效)
        SyncClient.ErrorReceived += OnKickError;
        SyncClient.AuthFailed += OnKickedAuthFailed;
    }

    /// <summary>登录/重连后应用云端宠物参数(换设备登录恢复一致)。</summary>
    private void OnWelcomeCloudParams(AuthSnapshot snap) => ApplyCloudParams(snap.Settings);

    /// <summary>同账号其他端修改宠物参数的回推:应用到本地配置。</summary>
    private void OnCloudSetting(string key, string value)
    {
        if (key == "WalkSpeed" || key == "EdgeMargin" || key == "Topmost")
            ApplyCloudParams(new Dictionary<string, string> { [key] = value });
    }

    /// <summary>把云端键值应用到 PetConfig(存在才覆盖,本地默认值兜底)。</summary>
    private static void ApplyCloudParams(Dictionary<string, string> settings)
    {
        if (settings.TryGetValue("WalkSpeed", out var ws)
            && double.TryParse(ws, NumberStyles.Float, CultureInfo.InvariantCulture, out var wsV))
            PetConfig.WalkSpeed = wsV;
        if (settings.TryGetValue("EdgeMargin", out var em)
            && double.TryParse(em, NumberStyles.Float, CultureInfo.InvariantCulture, out var emV))
            PetConfig.EdgeMargin = emV;
        if (settings.TryGetValue("Topmost", out var tp)
            && bool.TryParse(tp, out var tpV))
            PetConfig.Topmost = tpV;
    }

    /// <summary>
    /// 云端宠物尺寸到达(登录下发 = 云端权威;其他端修改推送 = 实时同步):
    /// 更新配置并通知小屋实时应用;小屋还没打开时 PetConfig 已生效,打开即用云端尺寸。
    /// </summary>
    private void OnCloudPetSize(double size)
    {
        PetConfig.PetSize = size;
        SaveSettings();
        _shell?.ChatView?.PetHouseHostView?.ApplyCloudSize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出前保存当前配置与离线缓存
        if (_settingsStore is not null)
            PetConfig.Save(_settingsStore);
        SaveLocalCache();
        SyncClient.Stop();
        base.OnExit(e);
    }

    private static void WriteCrash(Exception? ex)
    {
        try
        {
            if (ex is null)
                return;
            File.AppendAllText(CrashLog,
                $"\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===\n{ex}\n");
        }
        catch { }
    }

    /// <summary>立即把当前配置持久化(缩放等参数确认时调用,不等退出)。
    /// 本地 settings.json 仅作缓存,其余参数同步云端(尺寸走 petSize 帧,不在这里)。</summary>
    internal void SaveSettings()
    {
        if (_settingsStore is not null)
            PetConfig.Save(_settingsStore);
        if (SyncClient.IsConnected)
        {
            SyncClient.SetSetting("WalkSpeed", PetConfig.WalkSpeed.ToString(CultureInfo.InvariantCulture));
            SyncClient.SetSetting("EdgeMargin", PetConfig.EdgeMargin.ToString(CultureInfo.InvariantCulture));
            SyncClient.SetSetting("Topmost", PetConfig.Topmost.ToString());
        }
    }

    // ---- 视图切换 ----

    /// <summary>主窗口内容切换(视图缓存实例,保留聊天会话与宠物状态)。</summary>
    internal void NavigateTo(AppView view)
    {
        _shell?.ShowView(view);
    }

    // ---- 登录 / 自动登录 / 退出 ----

    /// <summary>登录(网络)。成功进主流程返回 null,失败返回中文错误。</summary>
    internal Task<string?> TryLoginAsync(string url, string account, string password)
    {
        _myAccount = account;
        return LoginWithAsync(url, "auth", account, password);
    }

    /// <summary>注册(成功后自动登录)。失败返回中文错误。</summary>
    internal Task<string?> TryRegisterAsync(string url, string account, string password, string nickname)
    {
        _myAccount = account;
        return LoginWithAsync(url, "register", account, password, nickname);
    }

    /// <summary>登录/注册公共等待:发连接请求,等 authOk / error,8 秒超时。</summary>
    private async Task<string?> LoginWithAsync(string url, string mode, string account, string password, string nickname = "")
    {
        var tcs = new TaskCompletionSource<string?>();

        Action<string>? fail = null;
        Action<UserInfo>? ok = null;
        void Unhook()
        {
            SyncClient.AuthFailed -= fail;
            SyncClient.AuthOk -= ok;
        }
        fail = reason => { Unhook(); tcs.TrySetResult(reason); };
        ok = _ => { Unhook(); tcs.TrySetResult(null); };
        SyncClient.AuthFailed += fail;
        SyncClient.AuthOk += ok;

        if (mode == "register")
            SyncClient.StartRegister(url, account, password, nickname);
        else
            SyncClient.Start(url, account, password);

        var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
        if (done != tcs.Task)
        {
            Unhook();
            return "无法连接服务器(地址可能不正确)";
        }
        var result = await tcs.Task;
        if (result is null)
            LoginSucceeded(account, password);
        return result;
    }

    /// <summary>登录成功:记身份、记住账号(下次登录页预填),主窗口切到聊天界面。</summary>
    private void LoginSucceeded(string account, string password)
    {
        _myAccount = account;
        _settingsStore?.Write("Account", account); // 下次登录预填账号
        _cacheSavedThisSession = false; // 首次全量历史拉完再落盘(缓存含完整消息)
        _currentCache = null;           // 在线登录:消息走服务器,不注入缓存
        NavigateTo(AppView.Chat);
    }

    /// <summary>
    /// 离线登录:加载本地缓存注入 SyncClient,直接进聊天界面。成功返回 null,失败返回中文错误。
    /// 同步方法(缓存文件小,读盘毫秒级)。
    /// </summary>
    internal string? TryOfflineLogin(string account)
    {
        var cache = _cacheStore?.Load(account);
        if (cache is null || cache.MyUser.UserId.Length == 0)
            return "未找到该账号的本地缓存,请先联网登录一次";
        _myAccount = account;
        _currentCache = cache;
        SyncClient.EnterOfflineMode(cache);
        ApplyCloudParams(cache.Snapshot.Settings); // 离线也恢复云端宠物参数(上次登录的)
        _settingsStore?.Write("Account", account); // 下次登录页预填该账号
        _cacheSavedThisSession = true; // 离线无历史拉取,不等待 HistoryLoaded
        NavigateTo(AppView.Chat);
        return null;
    }

    /// <summary>把 SyncClient 内存镜像落盘(登录后首次全量历史 / 登出 / 退出)。</summary>
    private void SaveLocalCache()
    {
        if (_myAccount.Length == 0 || _cacheStore is null || !SyncClient.IsConnected)
            return; // 未登录或离线模式:缓存无增量
        var c = SyncClient.Cache;
        c.Account = _myAccount;
        c.MyUser = SyncClient.MyUser;
        c.Snapshot = SyncClient.LastSnapshot;
        c.SavedAt = DateTimeOffset.Now;
        _cacheStore.Save(_myAccount, c);
    }

    /// <summary>登录会话首次 HistoryLoaded(全量历史拉完)落盘一次,之后增量只进内存镜像。</summary>
    private void OnHistoryLoadedSaveCache()
    {
        if (_cacheSavedThisSession || !SyncClient.IsConnected)
            return;
        SaveLocalCache();
        _cacheSavedThisSession = true;
    }

    /// <summary>退出登录:下线,回登录视图(账号保留,下次预填)。</summary>
    internal void Logout()
    {
        // 外出中登出:先隐藏悬浮窗并恢复小屋宠物,避免残留外出状态(小屋宠物"消失")
        if (_floatingWindow is { IsVisible: true })
        {
            _floatingWindow.Hide();
            _shell?.ChatView?.PetHouseHostView?.SetOutsideMode(false);
        }
        SaveLocalCache();   // 登出前把内存镜像落盘(增量消息/好友变化不丢)
        SyncClient.Logout(); // 通知服务器下线并断开(不再重连)
        _currentCache = null;
        NavigateTo(AppView.Login);
    }

    /// <summary>运行时收到 error 帧:仅禁用/删除踢人才强制下线(其他错误只影响当前操作)。</summary>
    private void OnKickError(string reason)
    {
        if (reason.Contains("禁用") || reason.Contains("删除"))
            ForceLogout(reason);
    }

    /// <summary>重连认证失败(已登录态):被禁用/删除/密码被改,凭据已失效,强制下线。</summary>
    private void OnKickedAuthFailed(string reason)
    {
        // 登录流程中的认证失败由 LoginWithAsync 处理(此时 MyUser 为空),此处只管已登录态
        if (SyncClient.MyUser.UserId.Length > 0)
            ForceLogout(reason);
    }

    /// <summary>被服务器强制下线:清登录态,回登录页并显示原因(连接已被服务端摘除,不发下线帧)。</summary>
    private void ForceLogout(string reason)
    {
        // 外出中登出:先隐藏悬浮窗并恢复小屋宠物(与 Logout 一致)
        if (_floatingWindow is { IsVisible: true })
        {
            _floatingWindow.Hide();
            _shell?.ChatView?.PetHouseHostView?.SetOutsideMode(false);
        }
        SaveLocalCache();
        SyncClient.Logout(); // 断开并清身份(此后 AuthFailed 重入因 MyUser 为空被忽略)
        _currentCache = null;
        NavigateTo(AppView.Login);
        _shell?.LoginView?.ShowKickMessage(reason); // 先导航确保登录视图已创建,再显示原因
    }

    /// <summary>退出整个应用(悬浮窗"退出桌宠"调用)。</summary>
    internal void Quit() => Shutdown();

    /// <summary>置顶开关即时生效:悬浮窗开着就直接应用(不用等下次外出重开)。</summary>
    internal void SyncFloatingTopmost()
    {
        if (_floatingWindow is { IsVisible: true })
            _floatingWindow.Topmost = PetConfig.Topmost;
    }

    // ---- 外出玩耍 / 回家 ----

    /// <summary>
    /// 外出玩耍:宠物从小屋隐藏(主窗口保留,聊天继续可用),延迟 200ms 再唤起
    /// 悬浮宠物(短暂过渡即接续),位置连续接续。先读状态再隐藏宠物:
    /// 走动 tick 缓存值在隐藏后仍有效。
    /// </summary>
    internal async void StartFloatingMode()
    {
        // 宠物小屋内嵌在主界面的宠物页签里,经 ChatView 取实例
        if (_shell?.ChatView?.PetHouseHostView is not { } petHouse)
            return;

        // 取状态要在隐藏之前:补算坐标后读取宠物屏幕位置与朝向
        var (x, y, facing) = petHouse.CaptureScreenState();
        petHouse.SetOutsideMode(true); // 宠物隐藏,小屋留空(不隐藏主窗口)

        // 宠物隐藏后延迟 200ms 再显示悬浮宠物(快速接续)
        await Task.Delay(200);
        if (_floatingWindow is null)
        {
            _floatingWindow = new PetFloatingWindow();
            _floatingWindow.ApplyPetSize(); // 小屋缩放后:悬浮窗应用最新尺寸
            _floatingWindow.SetPendingState(x, y, facing);
            _floatingWindow.Show();
        }
        else
        {
            _floatingWindow.ApplyPetSize();
            _floatingWindow.SetPendingState(x, y, facing);
            _floatingWindow.Show();
        }
    }

    /// <summary>
    /// 回家:悬浮宠物回小屋。先读悬浮窗状态(位置/朝向),再隐藏悬浮窗,
    /// 主窗口切回宠物小屋页签,宠物恢复显示并从与外面一致的位置接续。
    /// </summary>
    internal void GoHomeFromFloating()
    {
        if (_floatingWindow is not { IsVisible: true } || _shell is null)
            return;

        // 悬浮窗宠物中心屏幕坐标:窗口左缘 + 半宽
        var (posX, facing) = _floatingWindow.CaptureStatus();
        var centerX = _floatingWindow.Left + _floatingWindow.Width / 2;
        _floatingWindow.Hide();

        // 主窗口一直在(外出不再隐藏):被最小化/失去焦点时恢复前台,切回宠物页签
        if (_shell.WindowState == WindowState.Minimized)
            _shell.WindowState = WindowState.Normal;
        _shell.Activate();
        if (_shell.ChatView is { } chat)
        {
            chat.ShowPetTab(); // 切到宠物页签(IsVisibleChanged 自动重启走动计时器)
            chat.PetHouseHostView?.SetOutsideMode(false); // 先恢复宠物显示,再定位
            chat.PetHouseHostView?.RestoreFromFloating(centerX, facing);
        }
    }
}
