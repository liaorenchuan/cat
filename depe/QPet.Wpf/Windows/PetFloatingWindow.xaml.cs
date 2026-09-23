using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QPet.Core;

namespace QPet.Wpf.Windows;

/// <summary>
/// 悬浮宠物(外出玩耍形态):透明悬浮、置顶、贴屏幕底部,沿屏幕底部来回走动。
/// 交互:左键按住拖拽拎起;右键弹出菜单(回家 / 置顶切换 / 动作 / 退出桌宠)。
/// 「回家」:关闭悬浮窗,宠物回到主窗口宠物小屋(主窗口一直保留)。
/// 走动逻辑由 QPet.Core.PetWalker 引擎驱动,本窗口只负责摆画面。
/// </summary>
public partial class PetFloatingWindow : Window
{
    private readonly PetWalkDriver _drive = new(new PetWalker { Speed = PetConfig.WalkSpeed });
    private (double X, double Y, bool Facing)? _pendingState; // 外出玩耍时恢复的位置

    public PetFloatingWindow()
    {
        InitializeComponent();

        // 尺寸与置顶跟随配置(构造时 PetConfig 已从 settings.json 加载)
        Width = PetConfig.PetSize + 20;
        Height = PetConfig.PetSize + 40;
        Topmost = PetConfig.Topmost;

        // 掉头时切换走动帧并镜像朝向
        _drive.FacingChanged += ApplyFacing;
        // 每帧推进后把窗口左缘对齐引擎位置(取整到像素栅格:直接赋 double 会产生亚像素位置,窗口抖个不停)
        _drive.PositionChanged += _ => Left = Math.Round(_drive.Walker.Position);
        // 确认拖拽:显示被拖拽帧(onEnter 已暂停走动与眨眼)
        _drag.DragConfirmed += () => _effects?.Mood.Enter(PetMood.Drag);
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

    private PetEffects? _effects;     // 主图 + 眨眼 + 情绪控制器(Loaded 后创建)
    private readonly PetDragTracker _drag = new(); // 拖拽状态机(按下基准 + 阈值确认 + 增量)

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 主图 + 眨眼 + 情绪控制器一起接线(动作态进入暂停走动,恢复常态站住眨眼)
        _effects = new PetEffects(PetImage,
            onEnter: () =>
            {
                if (_effects?.Mood.Mood == PetMood.Walk)
                {
                    // 走路动作:从当前坐标开始走(常态站住时引擎位置与窗口位置不同步),
                    // 按朝向显示 8 图(左)或 8 镜像(右)
                    _drive.Walker.Position = Left;
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
        // 悬停在宠物上显示手型,提示可拎起;移出恢复默认指针
        MouseEnter += (_, _) => Cursor = Cursors.Hand;
        MouseLeave += (_, _) => Cursor = Cursors.Arrow;

        // 自然眨眼:窗口可见且常态时循环,隐藏即停
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                if (_effects?.Mood.Mood == PetMood.Normal)
                    _effects?.Blink.Start();
            }
            else
                _effects?.Blink.Stop();
        };
        _effects?.Blink.Start();

        // 外出场景:位置已由 SetPendingState 在 Show 前定好,这里只恢复状态,不再 Dock 覆盖
        if (_pendingState is { } s)
        {
            _pendingState = null;
            ApplyState(s.X, s.Y, s.Facing);
        }
        else
        {
            DockToWorkAreaBottom();
        }
    }

    /// <summary>
    /// 设置恢复状态:悬浮窗从该屏幕位置继续走(宠物小屋"外出玩耍"时调用)。
    /// 窗口已加载则立即生效,否则等 Loaded 后再应用。
    /// </summary>
    internal void SetPendingState(double centerX, double centerY, bool facingRight)
    {
        if (IsLoaded)
        {
            ApplyState(centerX, centerY, facingRight);
        }
        else
        {
            _pendingState = (centerX, centerY, facingRight);
            // Show 前直接定位:否则窗口先在默认位置(左上角)渲染,Loaded 时才跳过来,
            // 用户看到"外出一瞬间闪一下/抖动"
            Left = Math.Round(centerX - Width / 2);
            Top = centerY - Height / 2;
        }
    }

    /// <summary>
    /// 悬浮形态下 Position 即窗口左缘;宠物在窗口内居中(窗口宽 = 宠物宽 + 20)。
    /// 恢复时按宠物中心反推窗口 Left/Top,保证与宠物小屋同一中心点。
    /// Left 取整:窗口位置是像素栅格,小数像素会导致走动时抖动。
    /// </summary>
    private void ApplyState(double centerX, double centerY, bool facingRight)
    {
        _drive.Walker.Restore(centerX - Width / 2, facingRight);
        Left = Math.Round(_drive.Walker.Position);
        Top = centerY - Height / 2;
    }

    /// <summary>
    /// 应用配置的宠物尺寸与置顶(小屋缩放/置顶切换后调用,Show 前执行)。
    /// 只改尺寸/置顶与走动范围,位置由 Dock / ApplyState 决定,避免覆盖恢复位置。
    /// </summary>
    internal void ApplyPetSize()
    {
        Topmost = PetConfig.Topmost; // 置顶开关即时生效(不依赖下次 Show)
        var size = PetConfig.PetSize;
        PetImage.Width = size;
        PetImage.Height = size;
        PetScale.CenterX = size / 2;
        PetScale.CenterY = size / 2;
        Width = size + 20;
        Height = size + 40;
        // 走动范围跟随新宽度,位置收敛防出界
        var wa = SystemParameters.WorkArea;
        _drive.Walker.Max = wa.Right - Width;
        _drive.Walker.Position = Math.Min(_drive.Walker.Position, _drive.Walker.Max);
    }

    /// <summary>窗口贴工作区底部、水平居中,并设定走动范围。</summary>
    private void DockToWorkAreaBottom()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Bottom - Height;

        _drive.Walker.Min = wa.Left;
        _drive.Walker.Max = wa.Right - Width;
    }

    // ---- 走动(引擎驱动,挂渲染帧) ----

    /// <summary>开始走动:fromCenter 时从范围中点启动(范围无效不启动)。</summary>
    private void StartWalking(bool fromCenter = false) => _drive.StartWalking(fromCenter);

    /// <summary>停止走动:卸载渲染帧驱动。</summary>
    private void StopWalking() => _drive.StopWalking();

    /// <summary>当前宠物状态(供回家切换读取)。</summary>
    internal (double PositionX, bool FacingRight) CaptureStatus() =>
        (_drive.Walker.Position, _drive.Walker.FacingRight);

    // ---- 拖拽与点击 ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // 按下只记录基准、捕获鼠标,不切状态:确认有位移才算拖拽,否则松手是点击,
        // 双击 / 单击都不会闪现拖拽帧
        _drag.Press(PetDragTracker.CursorScreenPosition(), Left, Top);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured)
            return;

        var delta = _drag.Move(PetDragTracker.CursorScreenPosition());
        if (delta is null)
            return; // 未确认拖拽(手抖不动)或刚确认的基准帧:位置不动

        // 绝对定位:按下时的窗口位置 + 鼠标位移,不累积误差、不自激
        var dpi = VisualTreeHelper.GetDpi(this); // 屏幕像素 -> 窗口 DIP 单位
        Left = _drag.PosX + delta.Value.X / dpi.DpiScaleX;
        Top = _drag.PosY + delta.Value.Y / dpi.DpiScaleY;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
            return;

        ReleaseMouseCapture();
        // 放下后:仍在宠物上则恢复点击手势,否则默认指针
        Cursor = IsMouseOver ? Cursors.Hand : Cursors.Arrow;

        if (_drag.Dragging)
        {
            // 放下:同步拖拽后的实际位置,从当前位置继续走(不重置回中心)
            _drive.Walker.Position = Left;
            _effects?.Mood.Exit();
        }
        else
        {
            // 点击:被戳一下(睡觉中点击同时唤醒)
            _effects?.Mood.Enter(PetMood.Clicked, PetMoodController.ClickedDuration);
        }
    }

    // ---- 右键菜单 ----

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        UpdateMenuTexts();
        MenuPopup.IsOpen = true;
    }

    /// <summary>回家:关闭悬浮窗,主窗口恢复并切回宠物小屋。</summary>
    private void OnGoHome(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        (Application.Current as App)?.GoHomeFromFloating();
    }

    // ---- 状态动作(右键菜单) ----

    /// <summary>关掉右键菜单(StaysOpen=False 的 Popup 点按钮不会自动关)。</summary>
    private void CloseMenu() => MenuPopup.IsOpen = false;

    private void OnWalk(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        if (_effects?.Mood.Mood == PetMood.Walk)
            _effects?.Mood.Exit(); // 走路中再点:停步回常态
        else
            _effects?.Mood.Enter(PetMood.Walk);
    }

    private void OnHeart(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _effects?.Mood.Enter(PetMood.Heart, PetMoodController.HeartDuration);
    }

    private void OnKneel(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _effects?.Mood.Enter(PetMood.Kneel, PetMoodController.ActionDuration);
    }

    private void OnEat(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _effects?.Mood.Enter(PetMood.Eat, PetMoodController.ActionDuration);
    }

    private void OnSleep(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        if (_effects?.Mood.Mood == PetMood.Sleep)
            _effects?.Mood.Exit(); // 再点一次唤醒
        else
            _effects?.Mood.Enter(PetMood.Sleep);
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.Quit();
    }

    /// <summary>同步睡觉菜单按钮的文字(带状态;置顶已移至小屋左栏)。</summary>
    private void UpdateMenuTexts()
    {
        SleepButton.Content = _effects?.Mood.Mood == PetMood.Sleep ? "😴 醒来" : "😴 睡觉";
    }
}
