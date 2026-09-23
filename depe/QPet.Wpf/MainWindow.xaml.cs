using System.Windows;
using System.Windows.Controls;
using QPet.Wpf.Views;

namespace QPet.Wpf;

/// <summary>主窗口内容视图。</summary>
internal enum AppView
{
    Login, // 登录 / 注册
    Chat,  // 主界面(顶栏 + 消息/好友/群聊/宠物 四页签)
}

/// <summary>
/// 主窗口(壳):整个应用唯一窗口,内部按 AppView 缓存切换两个视图。
/// 宠物小屋已内嵌在 ChatView 的宠物页签里(外出悬浮窗经 ChatView.PetHouseHostView 联动)。
/// 关闭本窗口 = 退出整个应用(ShutdownMode.OnMainWindowClose,悬浮宠物同步关闭)。
/// </summary>
public partial class MainWindow : Window
{
    private readonly App _app;
    private LoginView? _login;
    private ChatView? _chat;

    public MainWindow(App app)
    {
        InitializeComponent();
        _app = app;
    }

    /// <summary>切换主窗口内容(视图缓存实例,保留聊天会话/宠物走动状态)。</summary>
    internal void ShowView(AppView view)
    {
        UserControl content = view switch
        {
            AppView.Login => _login ??= new LoginView(_app, _app.SettingsStore),
            AppView.Chat => _chat ??= new ChatView(_app),
            _ => throw new ArgumentOutOfRangeException(nameof(view)),
        };
        // 先挂新视图再切走旧视图:宠物页签切走时停走动计时器,回来时 IsVisibleChanged 恢复
        RootContent.Content = content;
    }

    /// <summary>主界面(聊天+宠物四页签),悬浮窗外出/回家经其内部宠物小屋联动。</summary>
    internal ChatView? ChatView => _chat;

    /// <summary>登录视图(被强制下线后由 App 传入提示原因)。</summary>
    internal LoginView? LoginView => _login;
}
