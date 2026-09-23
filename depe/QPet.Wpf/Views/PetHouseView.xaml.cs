using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QPet.Core;

namespace QPet.Wpf.Views;

/// <summary>
/// 宠物小屋视图(主窗口内):宠物在窗口内左右走动互动。
/// 走动逻辑由 QPet.Core.PetWalker 引擎驱动,本视图只负责摆画面。
/// 「外出玩耍」:宠物从小屋隐藏(主窗口保留,聊天继续可用),唤起悬浮窗(PetFloatingWindow)继续玩耍。
/// 「回家」(悬浮窗上):宠物从小屋恢复显示,从悬浮窗位置接续。
/// </summary>
public partial class PetHouseView : UserControl
{
    private readonly App _app;
    private readonly PetWalkDriver _drive = new(new PetWalker { Speed = PetConfig.WalkSpeed });
    private readonly Random _rand = new();
    private double _targetY;      // 垂直行走目标(水平掉头时随机新目标,宠物可上下走到小屋四角)
    private bool _loaded;
    private bool _outside;        // 外出中:宠物隐藏只留提示(宠物在桌面悬浮窗)

    /// <summary>XAML 嵌入用(宠物页签):Application.Current 已初始化,取全局 App 实例。</summary>
    public PetHouseView() : this((App)Application.Current!) { }

    public PetHouseView(App app)
    {
        InitializeComponent();
        _app = app;
        // 掉头时切换走动帧并镜像朝向;同时随机一个新的垂直目标(走动时缓慢上下移动)
        _drive.FacingChanged += ApplyFacing;
        _drive.FacingChanged += _ =>
            _targetY = _drive.Walker.MinY + _rand.NextDouble() * Math.Max(0, _drive.Walker.MaxY - _drive.Walker.MinY);
        // 每帧推进:水平对齐引擎位置,垂直缓慢朝目标 Y 移动(水平掉头时随机新目标)
        _drive.PositionChanged += delta =>
        {
            PetWalk.X = _drive.Walker.Position;
            var stepY = PetConfig.WalkSpeed * 0.5 * delta;
            var diff = _targetY - PetWalk.Y;
            PetWalk.Y += Math.Sign(diff) * Math.Min(Math.Abs(diff), stepY);
            UpdateCenterCoords();
        };
        // 确认拖拽:显示拖拽帧(onEnter 已暂停走动与眨眼)
        _drag.DragConfirmed += () => _effects?.Mood.Enter(PetMood.Drag);
    }

    private PetEffects? _effects;     // 主图 + 眨眼 + 情绪控制器(Loaded 后创建)

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;

        // 主图 + 眨眼 + 情绪控制器一起接线(动作态进入暂停走动,恢复常态站住眨眼)
        _effects = new PetEffects(PetImage,
            onEnter: () =>
            {
                if (_effects?.Mood.Mood == PetMood.Walk)
                {
                    // 走路动作:从当前坐标开始走,按朝向显示 8 图(左)或 8 镜像(右)
                    _drive.Walker.Position = PetWalk.X;
                    StartWalking();
                    PetImage.Source = PetWalkFrames.For(_drive.Walker.FacingRight);
                }
                else
                {
                    StopWalking(); // 其他动作:站住
                }
            },
            onExit: () =>
            {
                StopWalking(); // 回常态:站住眨眼
                if (IsVisible)
                    _effects?.Blink.Start();
            });
        // 应用已保存的宠物尺寸(上次缩放确认的结果,打开即生效)
        _petSize = PetConfig.PetSize;
        ApplyPetSize();
        // 悬停在宠物上显示手型,提示可拎起;移出恢复默认指针
        PetImage.MouseEnter += (_, _) => Cursor = Cursors.Hand;
        PetImage.MouseLeave += (_, _) => Cursor = Cursors.Arrow;
        // 页签切换:可见性变化重启走动计时器(Unloaded 已停,回来时 _loaded 不重跑 OnLoaded,这里兜底)
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                if (_outside)
                    return; // 外出中:宠物在悬浮窗,小屋保持留空,不启动任何驱动
                UpdateCenterCoords(); // 折叠期 Loaded 时坐标未算(无 PresentationSource),可见时补算
                if (_effects?.Mood.Mood == PetMood.Walk)
                    StartWalking();
                else if (_effects?.Mood.Mood == PetMood.Normal)
                    _effects?.Blink.Start();
            }
            else
            {
                StopWalking();
                _effects?.Blink.Stop();
            }
        };
        _effects?.Blink.Start();

        UpdateWalkRange();
        // 常态站住(原图 + 眨眼):停在区域中间(垂直也在中部),走路是单独动作
        _drive.Walker.Position = (_drive.Walker.Min + _drive.Walker.Max) / 2;
        PetWalk.X = _drive.Walker.Position;
        _targetY = (_drive.Walker.MinY + _drive.Walker.MaxY) / 2;
        PetWalk.Y = _targetY;
        UpdateCenterCoords(); // 初始定位后先算一次坐标,外出玩耍时读取
        // 移动窗口/改变大小:屏幕坐标随之变化,实时刷新外出玩耍用的中心坐标
        if (Window.GetWindow(this) is { } win)
        {
            win.LocationChanged += (_, _) => UpdateCenterCoords();
            win.SizeChanged += (_, _) => UpdateCenterCoords();
            UpdateTopmostText(); // 左栏置顶开关按当前悬浮窗置顶配置显示
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 切走(回聊天 / 外出玩耍):停走动驱动与眨眼,回来时 OnLoaded 不重跑(视图缓存)
        StopWalking();
        _effects?.Blink.Stop();
        // 缩放模式下切走:还原未确认的尺寸并退出缩放模式,否则回来拖拽仍当缩放
        if (_scaling)
        {
            _petSize = _petSizeBase;
            ApplyPetSize();
            ExitScaleMode();
        }
    }

    /// <summary>
    /// 朝向变化:切换走路帧(朝左 = 8 原图、朝右 = 8 翻转版),画面方向由素材自带,
    /// 不再需要整体镜像。只有走路动作态显示走路帧,常态画面是原图,不切。
    /// </summary>
    private void ApplyFacing(bool facingRight)
    {
        if (_effects?.Mood.Mood == PetMood.Walk)
            PetImage.Source = PetWalkFrames.For(facingRight);
    }

    /// <summary>视图尺寸变化时重算走动范围,宠物不会走出活动区。</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
            UpdateWalkRange();
    }

    private void UpdateWalkRange()
    {
        // 可走范围覆盖小屋(宠物区)四角:上下左右都贴活动区 Border 边距(8,12,12,12),
        // 宠物四角坐标可达小屋四角
        _drive.Walker.Min = 8;
        _drive.Walker.Max = Math.Max(_drive.Walker.Min, PetArea.ActualWidth - PetConfig.PetSize - 12);
        _drive.Walker.MinY = 12;
        _drive.Walker.MaxY = Math.Max(_drive.Walker.MinY, PetArea.ActualHeight - PetConfig.PetSize - 12);
        // 区域变化时把当前垂直位置/目标钳回新范围(防止变小越界)
        _targetY = Math.Clamp(_targetY, _drive.Walker.MinY, _drive.Walker.MaxY);
        PetWalk.Y = Math.Clamp(PetWalk.Y, _drive.Walker.MinY, _drive.Walker.MaxY);
    }

    // ---- 走动(引擎驱动,挂渲染帧) ----

    private void StartWalking(bool fromCenter = false)
    {
        if (!_drive.StartWalking(fromCenter))
            return;
        if (fromCenter)
        {
            // 从中间启动:垂直也回中
            _targetY = (_drive.Walker.MinY + _drive.Walker.MaxY) / 2;
            PetWalk.Y = _targetY;
        }
    }

    /// <summary>停止走动:卸载渲染帧驱动。</summary>
    private void StopWalking() => _drive.StopWalking();

    /// <summary>缓存宠物中心在屏幕上的坐标(供外出玩耍切换读取;走动 tick / 初始定位时更新)。</summary>
    private void UpdateCenterCoords()
    {
        // PointToScreen 在折叠/未挂载/窗口首次渲染阶段都会抛"Visual 未连接到 PresentationSource"
        // (页签初始 Collapsed 时 Loaded 已触发,渲染早期 FromVisual 非空但 PointToScreen 仍失败)。
        // 坐标只是外出玩耍的参考值,算不出就保持旧值,绝不中断 OnLoaded/渲染管线。
        if (PresentationSource.FromVisual(this) is null)
            return;
        try
        {
            // PointToScreen 含窗口边框/DPI,精确到系统坐标;除以 DPI 缩放得到与窗口 Left/Top 同单位的 DIP
            var dpi = VisualTreeHelper.GetDpi(this);
            var screen = PetImage.PointToScreen(new Point(PetImage.ActualWidth / 2, PetImage.ActualHeight / 2));
            _centerX = screen.X / dpi.DpiScaleX;
            _centerY = screen.Y / dpi.DpiScaleY;
        }
        catch (InvalidOperationException)
        {
            // 渲染早期 FromVisual 非空但 PointToScreen 仍失败:忽略,后续可见/走动 tick 会补算
        }
    }

    // ---- 点击与拖拽(只在宠物身上生效) ----

    private readonly PetDragTracker _drag = new(); // 拖拽状态机(按下基准 + 阈值确认 + 增量)

    // ---- 缩放模式 ----

    private bool _scaling;                // 缩放模式(拖拽宠物 / 缩放条调整大小)
    private bool _syncingSlider;          // 程序回写滑块值中(避免 ValueChanged 回环)
    private double _petSize;              // 当前宠物尺寸(缩放中实时变化)
    private double _petSizeBase;          // 进入缩放模式时的尺寸(取消时还原)
    private double _pressSize;            // 按下时的宠物尺寸(调大小基准)

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // 只对宠物本体生效:点活动区空白 / 控制栏不触发
        var pos = e.GetPosition(PetImage);
        if (pos.X < 0 || pos.X > PetImage.ActualWidth || pos.Y < 0 || pos.Y > PetImage.ActualHeight)
            return;

        // 按下只记录基准、捕获鼠标,不切状态:确认有位移才算拖拽,否则松手是点击,
        // 双击 / 单击都不会闪现拖拽帧
        _drag.Press(PetDragTracker.CursorScreenPosition(), PetWalk.X, PetWalk.Y);

        // 缩放模式:拖拽改为调整宠物大小(以按下时尺寸为基准,下拉变大)
        if (_scaling)
        {
            _pressSize = _petSize;
            CaptureMouse();
            return;
        }
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured)
            return;

        var pt = PetDragTracker.CursorScreenPosition();
        var dpi = VisualTreeHelper.GetDpi(this); // 屏幕像素 -> 窗口 DIP 单位

        // 缩放模式:按下时尺寸 + 垂直位移(下拉变大,上推变小)
        if (_scaling)
        {
            _petSize = Math.Clamp(_pressSize + (pt.Y - _drag.ScreenPress.Y) / dpi.DpiScaleY, 80, 400);
            ApplyPetSize();
            UpdateCenterCoords(); // 尺寸变化后同步屏幕坐标,外出玩耍时读取准确
            return;
        }

        var delta = _drag.Move(pt);
        if (delta is null)
            return; // 未确认拖拽(手抖不动)或刚确认的基准帧:位置不动

        // 拖拽中:宠物跟随鼠标在活动区内移动(窗口本身不动),拖不出可走范围(含上下)
        PetWalk.X = Math.Clamp(_drag.PosX + delta.Value.X / dpi.DpiScaleX,
            _drive.Walker.Min, _drive.Walker.Max);
        PetWalk.Y = Math.Clamp(_drag.PosY + delta.Value.Y / dpi.DpiScaleY,
            _drive.Walker.MinY, _drive.Walker.MaxY);
        UpdateCenterCoords(); // 拖拽中同步屏幕坐标,外出玩耍时读取准确
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
            return;

        ReleaseMouseCapture();
        if (_scaling)
            return; // 缩放模式:松开只结束本次调大小,等 OK / 取消收尾

        // 放下后:仍在宠物上则恢复点击手势,否则默认指针
        Cursor = IsMouseOver ? Cursors.Hand : Cursors.Arrow;

        if (_drag.Dragging)
        {
            // 放下:同步引擎位置(继续走动从放下处开始),垂直目标 = 放下处(走动时上下保持)
            _drive.Walker.Position = PetWalk.X;
            _targetY = PetWalk.Y;
            _effects?.Mood.Exit();
        }
        else
        {
            // 点击:被戳一下(睡觉中点击同时唤醒)
            _effects?.Mood.Enter(PetMood.Clicked, PetMoodController.ClickedDuration);
        }
    }

    // ---- 缩放模式控制 ----

    /// <summary>进入 / 退出缩放模式(左栏开关,再点一次 = 取消,还原尺寸)。</summary>
    private void OnScale(object sender, RoutedEventArgs e)
    {
        if (_scaling)
        {
            // 取消:还原进入时的尺寸
            _petSize = _petSizeBase;
            ApplyPetSize();
            ExitScaleMode();
            return;
        }

        _scaling = true;
        _petSize = PetImage.Width;
        _petSizeBase = _petSize;
        _effects?.Mood.Exit();   // 回常态站住
        StopWalking();
        ApplyPetSize();
        ScalePanel.Visibility = Visibility.Visible;
        ScaleButton.Content = "✖"; // 左栏开关文字:再点一次取消
        ScaleText.Text = "取消缩放";
    }

    /// <summary>确认缩放:保存尺寸到配置并立即持久化,上传云端,更新走动范围,退出缩放模式。</summary>
    private void OnScaleOk(object sender, RoutedEventArgs e)
    {
        PetConfig.PetSize = _petSize;
        _app.SaveSettings(); // 立即写入 settings.json,下次打开加载
        if (App.SyncClient.IsConnected)
            App.SyncClient.UpdatePetSize(_petSize); // 上传云端:同账号多端实时同步(服务器会推回确认)
        UpdateWalkRange();
        _drive.Walker.Position = Math.Min(_drive.Walker.Position, _drive.Walker.Max); // 尺寸变小防越界
        ExitScaleMode();
    }

    /// <summary>
    /// 云端尺寸到达(登录下发 / 同账号其他端修改推送):未在缩放模式时立即应用。
    /// 缩放确认后服务器推回的确认帧走这里:值相同,无感跳过。
    /// </summary>
    internal void ApplyCloudSize()
    {
        if (_scaling)
            return; // 用户正在手动缩放,不打扰;下次确认时以本地(用户正在调)为准
        if (Math.Abs(_petSize - PetConfig.PetSize) < 0.01)
            return;
        _petSize = PetConfig.PetSize;
        ApplyPetSize();
        UpdateWalkRange();
        _drive.Walker.Position = Math.Min(_drive.Walker.Position, _drive.Walker.Max); // 变小防越界
        _targetY = Math.Min(_targetY, _drive.Walker.MaxY);
        PetWalk.Y = Math.Min(PetWalk.Y, _drive.Walker.MaxY);
    }

    /// <summary>
    /// 应用当前尺寸到宠物画面(镜像中心跟随尺寸);
    /// 缩放模式中同步缩放条与尺寸文字(滑块 / 宠物拖拽双向同步)。
    /// </summary>
    private void ApplyPetSize()
    {
        PetImage.Width = _petSize;
        PetImage.Height = _petSize;
        PetScale.CenterX = _petSize / 2;
        PetScale.CenterY = _petSize / 2;
        SizeText.Text = $"{_petSize:F0} px";
        if (_scaling && Math.Abs(SizeSlider.Value - _petSize) > 0.5)
        {
            _syncingSlider = true;
            SizeSlider.Value = _petSize;
            _syncingSlider = false;
        }
    }

    /// <summary>缩放条滑块变化:实时应用尺寸(程序回写 / 非缩放模式时跳过)。</summary>
    private void OnSizeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_scaling || _syncingSlider)
            return;
        _petSize = e.NewValue;
        ApplyPetSize();
        UpdateCenterCoords(); // 尺寸变化后同步屏幕坐标,外出玩耍时读取准确
    }

    /// <summary>退出缩放模式:隐藏确认面板,左栏开关恢复。</summary>
    private void ExitScaleMode()
    {
        _scaling = false;
        ScalePanel.Visibility = Visibility.Collapsed;
        ScaleButton.Content = "🔍";
        ScaleText.Text = "缩放";
    }

    // ---- 小屋操作 ----

    /// <summary>外出玩耍:宠物从小屋隐藏,唤起悬浮窗宠物继续玩耍(右键菜单项)。</summary>
    private void OnGoOut(object sender, RoutedEventArgs e) =>
        CloseMenu(() => _app.StartFloatingMode());

    /// <summary>外出/回家:隐藏/恢复小屋宠物。外出时停走动与眨眼(小屋留空),
    /// 回家时按当前情绪状态恢复(走路动作继续走,其余站住眨眼)。</summary>
    internal void SetOutsideMode(bool outside)
    {
        if (_outside == outside)
            return;
        _outside = outside;
        if (outside)
        {
            ActionMenuPopup.IsOpen = false; // 菜单可能还开着(点外出时)
            StopWalking();
            _effects?.Blink.Stop();
            PetHost.Visibility = Visibility.Collapsed;
            OutsideHint.Visibility = Visibility.Visible;
        }
        else
        {
            PetHost.Visibility = Visibility.Visible;
            OutsideHint.Visibility = Visibility.Collapsed;
            if (IsVisible)
            {
                if (_effects?.Mood.Mood == PetMood.Walk)
                    StartWalking();
                else
                    _effects?.Blink.Start();
            }
        }
    }

    /// <summary>
    /// 切换悬浮窗置顶(原悬浮窗右键菜单的置顶项,移至小屋左栏):
    /// 配置存 PetConfig(本地持久化);悬浮窗开着立即生效,没开则下次外出时生效。
    /// </summary>
    private void OnToggleTopmost(object sender, RoutedEventArgs e)
    {
        PetConfig.Topmost = !PetConfig.Topmost;
        _app.SaveSettings(); // 立即写入 settings.json(下次启动仍生效)
        _app.SyncFloatingTopmost(); // 悬浮窗开着:即时应用置顶,不用等重开
        UpdateTopmostText();
    }

    /// <summary>同步左栏置顶开关文字(进入页面 / 切换后)。</summary>
    private void UpdateTopmostText()
    {
        var topmost = PetConfig.Topmost;
        TopmostButton.Content = topmost ? "📌" : "🔝";
        TopmostText.Text = topmost ? "已置顶" : "置顶";
    }

    /// <summary>
    /// 悬浮窗"回家"时:恢复宠物到主窗口宠物小屋。
    /// 用悬浮窗的宠物屏幕位置比例映射到活动区(与它在外面的位置大致一致),朝向接续。
    /// </summary>
    internal void RestoreFromFloating(double centerX, bool facingRight)
    {
        var wa = SystemParameters.WorkArea;
        var ratio = (centerX - wa.Left) / Math.Max(1, wa.Width);
        _drive.Walker.Restore(_drive.Walker.Min + ratio * (_drive.Walker.Max - _drive.Walker.Min), facingRight);
        PetWalk.X = _drive.Walker.Position;
        // 悬浮窗宠物垂直居中,回家后也停在小屋垂直中部(Restore 触发掉头事件已随机目标,这里覆盖回中)
        _targetY = (_drive.Walker.MinY + _drive.Walker.MaxY) / 2;
        PetWalk.Y = _targetY;
        UpdateCenterCoords();
    }

    private double _centerX, _centerY; // 宠物中心的屏幕坐标缓存(走动 tick 实时更新,切走后仍可读)

    /// <summary>外出玩耍前捕获完整状态:先补算一次坐标(常态站住时走动 tick 不更新)。</summary>
    internal (double X, double Y, bool Facing) CaptureScreenState()
    {
        UpdateCenterCoords();
        return (_centerX, _centerY, _drive.Walker.FacingRight);
    }

    // ---- 动作按钮(与悬浮窗右键菜单动作一致) ----

    /// <summary>右键点宠物:弹出动作菜单(与悬浮窗右键菜单同款交互,菜单项共用手势)。</summary>
    private void OnPetRightClick(object sender, MouseButtonEventArgs e)
    {
        ActionMenuPopup.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>同步右键菜单按钮文字(睡觉 / 走路是开关状态)。</summary>
    private void UpdateActionButtons()
    {
        SleepMenuButton.Content = _effects?.Mood.Mood == PetMood.Sleep ? "😴 醒来" : "😴 睡觉";
        WalkMenuButton.Content = _effects?.Mood.Mood == PetMood.Walk ? "⏹ 停下" : "🚶 走路";
    }

    /// <summary>右键菜单项点击公共收尾:先关菜单再执行动作(点菜单按钮 Popup 不会自动关)。</summary>
    private void CloseMenu(Action action)
    {
        ActionMenuPopup.IsOpen = false;
        action();
    }

    private void OnHeart(object sender, RoutedEventArgs e) =>
        CloseMenu(() => _effects?.Mood.Enter(PetMood.Heart, PetMoodController.HeartDuration));

    private void OnKneel(object sender, RoutedEventArgs e) =>
        CloseMenu(() => _effects?.Mood.Enter(PetMood.Kneel, PetMoodController.ActionDuration));

    private void OnEat(object sender, RoutedEventArgs e) =>
        CloseMenu(() => _effects?.Mood.Enter(PetMood.Eat, PetMoodController.ActionDuration));

    private void OnSleep(object sender, RoutedEventArgs e) =>
        CloseMenu(() =>
        {
            if (_effects?.Mood.Mood == PetMood.Sleep)
                _effects?.Mood.Exit(); // 再点一次唤醒
            else
                _effects?.Mood.Enter(PetMood.Sleep);
            UpdateActionButtons();
        });

    private void OnWalk(object sender, RoutedEventArgs e) =>
        CloseMenu(() =>
        {
            if (_effects?.Mood.Mood == PetMood.Walk)
                _effects?.Mood.Exit(); // 走路中再点:停步回常态
            else
            {
                UpdateWalkRange(); // 缩放后走动范围按当前尺寸
                _effects?.Mood.Enter(PetMood.Walk);
            }
            UpdateActionButtons();
        });
}
