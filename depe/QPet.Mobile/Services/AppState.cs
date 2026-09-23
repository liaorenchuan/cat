using System;
using System.Threading.Tasks;
using QPet.Core;

namespace QPet.Mobile.Services;

/// <summary>
/// 全局登录状态(App 级单例,桌面 App.xaml.cs 登录区的移动端对应):
/// 包 SyncClient(事件已调度到 UI 线程),承载 登录/注册(8 秒超时)/离线登录/退出登录/被踢。
/// 登出与被踢前把内存缓存镜像落盘;被踢原因经 Kicked 事件广播(AppShell 导航回登录页显示)。
/// 启动加载宠物本地配置(PetConfig),云端尺寸到达经 CloudPetSize 广播给宠物页(桌面同款接线)。
/// </summary>
public static class AppState
{
    /// <summary>IM 客户端:构造注入 UI 线程调度器,所有事件在主线程触发,页面可直接订阅。</summary>
    public static SyncClient Client { get; } = new(action => MainThread.BeginInvokeOnMainThread(action));

    /// <summary>设置存储(MAUI Preferences,桌面 JsonSettingsStore 的安卓实现),键位同桌面:
    /// Account / ServerAddress。记住的密码不在其中(走 SecurePassword 加密存储,见 D2)。</summary>
    public static ISettingsStore Settings { get; } = new PreferencesStore();

    /// <summary>离线缓存:每账号一个 cache-账号.json,存应用数据目录(桌面存 exe 同目录)。</summary>
    private static readonly LocalCacheStore CacheStore = new(FileSystem.AppDataDirectory);

    /// <summary>当前登录账号(登出/被踢后清空,登录页据此预填)。</summary>
    public static string Account { get; private set; } = "";

    /// <summary>登录态(MyUser 有身份即已登录;离线登录同)。</summary>
    public static bool IsLoggedIn => Client.MyUser.UserId.Length > 0;

    /// <summary>被服务器强制下线(禁用/删除/凭据失效),参数为原因(UI 线程触发)。</summary>
    public static event Action<string>? Kicked;

    /// <summary>云端宠物尺寸到达(登录下发 / 确认回推 / 他端修改),已写配置并持久化(UI 线程触发)。</summary>
    public static event Action<double>? CloudPetSize;

    private static bool _cacheSavedThisSession; // 首次全量历史落盘后置位(桌面同款)

    static AppState()
    {
        PetConfig.Load(Settings); // 本地宠物配置先行(尺寸/步速),云端尺寸到达后覆盖
        Client.ErrorReceived += OnKickError;        // 禁用/删除 → 踢
        Client.AuthFailed += OnKickedAuthFailed;    // 已登录态重连认证失败 → 踢
        Client.HistoryLoaded += OnHistoryLoadedSaveCache; // 首次全量历史拉完落盘
        Client.PetSizeReceived += OnCloudPetSize;   // 宠物尺寸云同步(桌面 App.OnCloudPetSize 同款)
    }

    // ---- 退出 / 被踢 ----

    /// <summary>退出登录:落盘缓存 → 下线断开 → 清登录态(账号保留,登录页预填)。</summary>
    public static void Logout()
    {
        SaveLocalCache();
        Client.Logout();
        Account = "";
        _cacheSavedThisSession = false;
    }

    /// <summary>运行时收到 error 帧:仅禁用/删除踢人才强制下线,其他错误只影响当前操作(桌面同)。</summary>
    private static void OnKickError(string reason)
    {
        if (reason.Contains("禁用") || reason.Contains("删除"))
            ForceLogout(reason);
    }

    /// <summary>重连认证失败(已登录态):被禁用/删除/密码被改,凭据已失效,强制下线。
    /// 登录流程中的认证失败由 WaitAuthAsync 处理(此时 MyUser 为空),此处只管已登录态。</summary>
    private static void OnKickedAuthFailed(string reason)
    {
        if (Client.MyUser.UserId.Length > 0)
            ForceLogout(reason);
    }

    /// <summary>被服务器强制下线:清登录态并广播原因(连接已被服务端摘除,不发下线帧)。</summary>
    private static void ForceLogout(string reason)
    {
        SaveLocalCache();
        Client.Logout(); // 断开并清身份(此后 AuthFailed 重入因 MyUser 为空被忽略)
        Account = "";
        _cacheSavedThisSession = false;
        Kicked?.Invoke(reason);
    }

    /// <summary>云端宠物尺寸(登录下发 / 确定缩放回推 / 他端修改):写配置 + 持久化,再广播给宠物页。
    /// 桌面同款:任何来源的尺寸都直接采纳并保存,下次启动即本地配置。</summary>
    private static void OnCloudPetSize(double size)
    {
        PetConfig.PetSize = size;
        PetConfig.Save(Settings);
        CloudPetSize?.Invoke(size);
    }

    // ---- 登录 / 注册 / 离线登录 ----

    /// <summary>登录(网络)。成功返回 null,失败返回中文错误。</summary>
    public static Task<string?> LoginAsync(string url, string account, string password) =>
        WaitAuthAsync(url, "auth", account, password, "");

    /// <summary>注册(成功后服务器回 authOk 自动登录)。失败返回中文错误。</summary>
    public static Task<string?> RegisterAsync(string url, string account, string password, string nickname) =>
        WaitAuthAsync(url, "register", account, password, nickname);

    /// <summary>登录/注册公共等待:发连接请求,等 authOk / error,8 秒超时(桌面 LoginWithAsync 同款)。</summary>
    private static async Task<string?> WaitAuthAsync(string url, string mode, string account, string password, string nickname)
    {
        var tcs = new TaskCompletionSource<string?>();

        Action<string>? fail = null;
        Action<UserInfo>? ok = null;
        void Unhook()
        {
            Client.AuthFailed -= fail;
            Client.AuthOk -= ok;
        }
        fail = reason => { Unhook(); tcs.TrySetResult(reason); };
        ok = _ => { Unhook(); tcs.TrySetResult(null); };
        Client.AuthFailed += fail;
        Client.AuthOk += ok;

        if (mode == "register")
            Client.StartRegister(url, account, password, nickname);
        else
            Client.Start(url, account, password);

        var done = await Task.WhenAny(tcs.Task, Task.Delay(8000));
        if (done != tcs.Task)
        {
            Unhook();
            // 超时必须连连接一起掐掉:只解绑事件的话后台还在认证,迟到的 authOk 会把状态搞成
            // "停在登录页却已登录"(MyUser 有值、账号名为空),这个账号还会整场都不落盘
            Client.AbortSession();
            return "无法连接服务器(地址可能不正确)";
        }
        var result = await tcs.Task;
        if (result is null)
        {
            Account = account;
            _cacheSavedThisSession = false; // 全新会话:首次全量历史拉完再落盘(缓存含完整消息)
        }
        return result;
    }

    /// <summary>离线登录:加载该账号本地缓存注入客户端,直接进主界面(不连服务器、不校验密码)。
    /// 成功返回 null,失败返回中文错误。</summary>
    public static string? TryOfflineLogin(string account)
    {
        var cache = CacheStore.Load(account);
        if (cache is null)
            return "未找到该账号的本地缓存,请先联网登录一次";
        Account = account;
        Client.EnterOfflineMode(cache);
        _cacheSavedThisSession = true; // 离线无历史拉取,不等待 HistoryLoaded
        return null;
    }

    // ---- 离线缓存落盘 ----

    /// <summary>把 SyncClient 内存镜像落盘(首次全量历史 / 登出 / 被踢 / 退到后台)。桌面 SaveLocalCache 同款。
    /// 返回是否真的写成功(未登录/离线/写盘失败都返回 false)。</summary>
    private static bool SaveLocalCache()
    {
        if (Account.Length == 0 || !Client.IsConnected)
            return false; // 未登录或离线模式:缓存无增量
        var c = Client.Cache;
        c.Account = Account;
        c.MyUser = Client.MyUser;
        c.Snapshot = Client.LastSnapshot;
        c.SavedAt = DateTimeOffset.Now;
        return CacheStore.Save(Account, c);
    }

    /// <summary>退到后台/被系统回收前落盘(安卓生命周期钩子调用)。
    /// 没有这一步的话,离线缓存永远停在"登录那一刻":本次会话收到的消息/未读/备注全丢。</summary>
    public static void SaveCacheNow() => SaveLocalCache();

    /// <summary>登录会话首次 HistoryLoaded(全量历史拉完)落盘一次,之后增量只进内存镜像。</summary>
    private static void OnHistoryLoadedSaveCache()
    {
        if (_cacheSavedThisSession || !Client.IsConnected)
            return;
        // 只有真的写成功才置位:以前无条件置位,写盘失败(或账号还是空)会导致这场会话再也不落盘,
        // 下次离线登录就提示"未找到该账号的本地缓存"
        if (SaveLocalCache())
            _cacheSavedThisSession = true;
    }
}
