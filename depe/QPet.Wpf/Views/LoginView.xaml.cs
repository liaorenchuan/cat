using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QPet.Core;
using QPet.Ui;
using QPet.Wpf.Services;

namespace QPet.Wpf.Views;

/// <summary>
/// 登录 / 注册视图(主窗口内)。连局域网独立服务器(QPet.Server)走 WebSocket 认证。
/// 登录成功由 App 接管:主窗口内容切换为聊天界面。
/// 服务器地址:地址框预填记住的地址(默认本机),可手动改或点「自动检测」在局域网搜索,登录成功记住地址。
/// </summary>
public partial class LoginView : UserControl
{
    private readonly App _app;
    private readonly JsonSettingsStore? _store;
    private bool _registerMode;
    private bool _submitting;
    private bool _detecting; // 自动检测中:禁用按钮防重入

    // 登录奶黄 / 注册薄荷绿,两套渐变背景让模式一眼可辨(奶黄取 UiPalette,薄荷绿为注册模式专用)
    private static readonly Brush LoginBrush = new LinearGradientBrush(
        UiPalette.AppBgTopColor, UiPalette.AppBgColor, 90);
    private static readonly Brush RegisterBrush = new LinearGradientBrush(
        Color.FromRgb(0xF2, 0xFB, 0xF4), Color.FromRgb(0xDF, 0xF0, 0xE3), 90);

    public LoginView(App app, JsonSettingsStore? store)
    {
        InitializeComponent();
        _app = app;
        _store = store;
        AccountBox.Text = _store?.Read("Account") ?? "";
        // 服务器地址框:预填记住的地址(默认本机),登录成功由 Submit 记住新值
        ServerBox.Text = _store?.Read("ServerAddress") ?? $"127.0.0.1:{Protocol.DefaultPort}";
        // 记住的密码(DPAPI 加密,本机可解密):有就回填并勾选,没有则保持默认勾选待新密码
        var savedPassword = _store is null ? null : new CredentialService(_store).ReadPassword();
        if (savedPassword is not null)
        {
            PasswordBox.Password = savedPassword;
            RememberBox.IsChecked = true;
        }
    }

    /// <summary>打开即自动搜索局域网服务器(找到填入,未找到静默保留记住的地址);焦点落在视图自身(未点击不显示光标,回车提交仍有效)。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
        await AutoDetectAsync(quietOnFail: true);
    }

    /// <summary>显示错误提示并恢复提交按钮(仅本视图内部使用)。</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        SubmitButton.IsEnabled = true;
    }

    /// <summary>被服务器强制下线(禁用/删除):登录页显示原因(由 App 在被踢事件中调用)。</summary>
    internal void ShowKickMessage(string reason) => ShowError(reason);

    private void OnSubmitClicked(object sender, RoutedEventArgs e) => Submit();

    private void OnOfflineClicked(object sender, RoutedEventArgs e) => OfflineLogin();

    private void OnAutoDetect(object sender, RoutedEventArgs e) => _ = AutoDetectAsync();

    /// <summary>
    /// 自动检测局域网 QPet 服务器:向组播组发 QPET_DISCOVER 查询,服务器(01-Server 已监听)单播回复地址。
    /// 找到即填入地址框,由用户点登录;找不到提示手动输入(组播在 AP 隔离/防火墙下不可用)。
    /// quietOnFail:打开视图自动触发时未找到不弹红字(保留记住的地址,别吓到用户);手动点击才提示。
    /// </summary>
    private async Task AutoDetectAsync(bool quietOnFail = false)
    {
        if (_detecting)
            return;
        _detecting = true;
        AutoDetectButton.IsEnabled = false;
        AutoDetectButton.Content = "检测中...";
        ErrorText.Visibility = Visibility.Collapsed; // 清上次登录失败的残留提示(检测结果另行给出)
        try
        {
            var found = await ServerDiscovery.FindAsync(); // 限时 2 秒收集回复
            var addr = found.FirstOrDefault(a => a.Length > 0);
            if (addr is not null)
                ServerBox.Text = addr; // 找到即填入,直接点登录即可
            else if (!quietOnFail)
                ShowError("未发现 QPet 服务器,请确认服务器已启动后重试,或手动输入地址");
        }
        catch
        {
            if (!quietOnFail)
                ShowError("自动检测失败(组播不可用),请手动输入服务器地址");
        }
        finally
        {
            _detecting = false;
            AutoDetectButton.IsEnabled = true;
            AutoDetectButton.Content = "自动检测";
        }
    }

    /// <summary>本地离线登录:加载该账号的本地缓存直接进软件(不连服务器,不校验密码)。</summary>
    private void OfflineLogin()
    {
        if (_submitting)
            return;
        var account = AccountBox.Text.Trim();
        if (account.Length == 0)
        {
            ShowError("请输入账号(将使用该账号的本地缓存登录)");
            return;
        }
        var err = _app.TryOfflineLogin(account); // 同步:缓存文件小,读盘毫秒级
        if (err is not null)
            ShowError(err); // 无缓存 / 缓存损坏:提示先联网登录一次
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Submit();
    }

    /// <summary>切换登录 / 注册模式:整体换装(标题/背景/横幅/按钮配色),两界面一眼区分。</summary>
    private void OnToggleMode(object sender, RoutedEventArgs e)
    {
        _registerMode = !_registerMode;

        // 布局:注册多一行昵称 + 顶部欢迎横幅;离线按钮注册模式下隐藏(避免歧义)
        NicknamePanel.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        ModeBanner.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        OfflineButton.Visibility = _registerMode ? Visibility.Collapsed : Visibility.Visible;

        // 文案:标题 / 副标题 / 按钮(图标由 XAML 圆底固定显示,文案不再带 emoji)
        TitleText.Text = _registerMode ? "注册新账号" : "Q版桌宠";
        SubtitleText.Text = _registerMode
            ? "创建账号 · 自动加入公共群聊 · 昵称会显示在群里"
            : "局域网聊天 · 好友 · 群聊";
        SubmitButton.Content = _registerMode ? "注册并登录" : "登 录";
        ToggleModeButton.Content = _registerMode ? "已有账号? 去登录" : "没有账号? 注册";

        // 配色:奶黄(登录) vs 薄荷绿(注册)
        Background = _registerMode ? RegisterBrush : LoginBrush;
        SubmitButton.Background = _registerMode
            ? UiPalette.Green
            : UiPalette.Warm;
        CardBorder.BorderBrush = _registerMode
            ? new SolidColorBrush(Color.FromRgb(0xA8, 0xD5, 0xB0))
            : new SolidColorBrush(Color.FromRgb(0xF0, 0xD9, 0xBE));

        HideError();

        // 焦点跟到最上方输入框:注册=昵称(默认选中昵称),登录=视图(保持未点击不显光标)
        if (_registerMode)
            NicknameBox.Focus();
        else
            Focus();
    }

    private async void Submit()
    {
        if (_submitting)
            return;

        var account = AccountBox.Text.Trim();
        var password = PasswordBox.Password;
        if (account.Length == 0 || password.Length == 0)
        {
            ShowError("请输入账号和密码");
            return;
        }

        var url = ServerBox.Text.Trim(); // 服务器地址框(自动检测填入或手动改),为空回退默认本机
        if (url.Length == 0)
            url = $"127.0.0.1:{Protocol.DefaultPort}";

        string? err;
        _submitting = true;
        SubmitButton.IsEnabled = false;
        SubmitButton.Content = _registerMode ? "注册中..." : "登录中..."; // 请求期间按钮变加载态,给用户反馈
        try
        {
            if (_registerMode)
            {
                var nickname = NicknameBox.Text.Trim();
                if (nickname.Length == 0)
                {
                    ShowError("请输入昵称");
                    return;
                }
                err = await _app.TryRegisterAsync(url, account, password, nickname);
            }
            else
            {
                err = await _app.TryLoginAsync(url, account, password);
            }
        }
        finally
        {
            _submitting = false;
            SubmitButton.IsEnabled = true;
            SubmitButton.Content = _registerMode ? "注册并登录" : "登 录";
        }

        if (err is not null)
        {
            ShowError(err);
            return;
        }

        _store?.Write("ServerAddress", url); // 登录成功,记住地址(下次直接连)
        // 记住密码:勾选则 DPAPI 加密保存,取消勾选清除旧密码(下次不自动填)
        if (_store is not null)
        {
            var cred = new CredentialService(_store);
            if (RememberBox.IsChecked == true)
                cred.Save(password);
            else
                cred.Clear();
        }
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
