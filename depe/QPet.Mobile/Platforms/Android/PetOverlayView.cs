using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using QPet.Core;
using QPet.Mobile.Services;
using AndroidView = Android.Views.View;     // MAUI 隐式 using 带进了同名类型(View / Color),须别名区分
using AndroidColor = Android.Graphics.Color;

namespace QPet.Mobile;

/// <summary>
/// 系统层宠物(悬浮在其他应用上层):自绘素材帧 + 走动 + 拖拽 + 双击呼出菜单(菜单贴着宠物放,不压住它)。
/// 走动复用 Core 的 PetWalker(纯逻辑引擎,只管 X 轴往返);眨眼/动作帧沿用面板侧的时长约定
/// (比心 1 秒、跪下/吃面 3 秒、眨眼三步各 90ms)。窗口紧贴宠物尺寸,宠物以外的区域不挡下层触摸。
/// 菜单项与缩放面板都照着面板侧来(同序同字、同样 80-400),尺寸也统一按 PetConfig.PetSize(单位 dp)
/// 乘屏幕密度算像素 —— 屋里屋外看着必须是一只一样大的宠物。
/// </summary>
public class PetOverlayView : AndroidView
{
	private const int FrameMs = 16;          // 走动/渲染节拍(~60fps,与面板侧一致)
	private const int DragThresholdPx = 10;  // 拖拽确认阈值(照搬桌面 PetDragTracker)
	private const int DoubleTapWindowMs = 400; // 相邻两击的最大间隔(超时重新数;双击窗口要比三击短,免得"点一下隔会儿再点一下"误触)
	private const int MenuGapPx = 12;          // 菜单与宠物之间的空隙
	private static readonly int[] BlinkDelayRange = { 2500, 6000 };

	private readonly Context _context;
	private readonly Action _onGoHome;
	private readonly IWindowManager? _windowManager;
	private readonly Handler _handler = new(Looper.MainLooper!);
	private readonly PetWalker _walker = new();
	private readonly Random _rand = new();
	private readonly Dictionary<string, Bitmap> _frames = new();

	private readonly Action _tickAction;
	private readonly Action _blinkAction;

	private WindowManagerLayoutParams? _layout;
	private AndroidView? _menu;

	private Bitmap? _current;
	private string _currentName = "";
	private int _petSize;          // 宠物边长(像素):由 _petDp 乘屏幕密度换算,见 PetSizeFromDp
	private double _petDp;         // 宠物尺寸(dp):与面板 PetConfig.PetSize / 小屋滑块(80-400)同一单位
	private double _petDpBase;     // 进缩放前的尺寸(取消时还原)
	private bool _scaling;
	private AndroidView? _scaleUi;
	private TextView? _scaleText;
	private int _screenW, _screenH;
	private readonly Android.Graphics.Rect _visibleRect = new();
	private int _minX, _minY, _maxX, _maxY; // 可拖范围(窗口坐标),见 RefreshFrame
	private bool _frameLogged;
	private long _lastFrameCheckMs;
	private int _winX, _winY;
	private int _actionToken;      // 新动作作废旧动作的复位计时
	private bool _facingRight;     // 朝向:素材本身朝左,true = 水平镜像(朝右)。初值由出门那一刻小屋里的朝向决定
	private bool _pendingStartPos; // 出门落点还差一次"屏幕坐标 → 窗口坐标"的换算(见构造函数)
	private int _startScreenX, _startScreenY; // 面板给的落点(屏幕坐标,像素),换算用
	private bool _walking;         // 出门先站着(和在小屋里一样),菜单里点"走路"才走
	private bool _sleeping;
	private bool _dragging;
	private int _tapCount;         // 连点计数(窗口内满两下 = 呼出菜单)
	private long _lastTapMs;
	private bool _stopped;

	/// <param name="windowManager">由服务解析好的窗口管理器(见 PetOverlayService.ResolveWindowManager):
	/// 本类里直接用 GetSystemService + "as IWindowManager" 取恒为 null(返回的是 Java.Lang.Object 裸包装),
	/// 走动 / 拖拽 / 双击呼出菜单就会全被空引用挡掉 —— 宠物能显示却一动不动。</param>
	/// <param name="startScreenX">出门前宠物在小屋里的屏幕坐标(左上角),int.MinValue = 没给,按屏幕右下角兜底。</param>
	/// <param name="startFacingRight">出门前宠物在小屋里的朝向(true = 朝右)。不接过来会出现"外出后镜像反了":
	/// 小屋里站着/朝左走时画面未镜像,悬浮窗若默认朝右就会左右颠倒。</param>
	public PetOverlayView(Context context, Action onGoHome, IWindowManager windowManager,
		int startScreenX = int.MinValue, int startScreenY = int.MinValue, bool startFacingRight = false) : base(context)
	{
		_context = context;
		_onGoHome = onGoHome;
		_windowManager = windowManager;
		_facingRight = startFacingRight;

		_tickAction = Tick;
		_blinkAction = BlinkTick;

		SetWillNotDraw(false);
		_petDp = PetConfig.PetSize;
		_petSize = PetSizeFromDp(_petDp);
		var metrics = context.Resources?.DisplayMetrics;
		_screenW = metrics?.WidthPixels ?? 1080;
		_screenH = metrics?.HeightPixels ?? 1920;

		// 可拖范围先按整屏兜底,窗口真正布局出来后由 RefreshFrame 校正到系统栏之间
		_minX = 0;
		_minY = 0;
		_maxX = Math.Max(0, _screenW - _petSize);
		_maxY = Math.Max(0, _screenH - _petSize);

		_winX = Math.Clamp(_screenW - _petSize - Dp(24), _minX, _maxX);
		_winY = Math.Clamp(_screenH * 2 / 3, _minY, _maxY);
		if (startScreenX != int.MinValue && startScreenY != int.MinValue)
		{
			// 面板给的落点是**屏幕**坐标,窗口坐标以可见区左上角为原点(差一个状态栏高),
			// 可这会儿窗口还没挂上系统、量不到可见区 —— 硬摆就会先摆错(宠物出门先下移一个状态栏高),
			// 所以落点先记下来,等 RefreshFrame 拿到可见区再一次性换算(见 _pendingStartPos)。
			_startScreenX = startScreenX;
			_startScreenY = startScreenY;
			_pendingStartPos = true;
		}

		// 走动范围:整屏横向;引擎只管 X,纵向跟窗口当前位置
		_walker.Min = _minX;
		_walker.Max = _maxX;
		_walker.Speed = PetConfig.WalkSpeed;
		_walker.Restore(_winX, _facingRight);
		_walker.DirectionChanged += right =>
		{
			_facingRight = right;
			Invalidate();
		};

		AppState.CloudPetSize += OnCloudPetSize; // 小屋/他端改了尺寸:悬浮窗跟着变,免得屋里屋外不一样大

		ShowFrame("pet");
		ScheduleTick();
		StartBlink();
	}

	/// <summary>云端尺寸到达(配置已由 AppState 持久化):跟着换尺寸,正在手动拖滑块时不打扰。</summary>
	private void OnCloudPetSize(double size)
	{
		if (_scaling || Math.Abs(size - _petDp) < 0.01)
			return;
		_petDp = size;
		ApplyPetSize();
	}

	public WindowManagerLayoutParams CreateLayoutParams()
	{
		var type = OperatingSystem.IsAndroidVersionAtLeast(26)
			? WindowManagerTypes.ApplicationOverlay   // Android 8+ 专用悬浮窗类型
			: WindowManagerTypes.Phone;               // 旧机型(项目支持 API 21+)
		// 不要加 LayoutNoLimits:它会让系统把"窗口可见区域"报成无限制的
		// [-100000,-100000][100000,100000](dumpsys 的 display= 一栏同款),
		// 于是 getWindowVisibleDisplayFrame 拿不到真实可用高度,clamp 上界失守。
		var lp = new WindowManagerLayoutParams(
			_petSize, _petSize, type,
			WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal,
			Format.Translucent)
		{
			Gravity = GravityFlags.Top | GravityFlags.Left,
			X = _winX,
			Y = _winY,
		};
		// 落点还没换算(见 _pendingStartPos):先在兜底位置挂出去,但别露脸 ——
		// 不然宠物出门会从屏幕下方"走"到落点,看着就是"出门后自己走了一段"。
		if (_pendingStartPos)
			lp.Alpha = 0f;
		_layout = lp;
		return lp;
	}

	/// <summary>服务销毁前调用:停掉所有计时器与菜单(窗口移除由服务负责)。</summary>
	public void Stop()
	{
		_stopped = true;
		AppState.CloudPetSize -= OnCloudPetSize; // 静态事件:不退订会一直挂着这个已销毁的 View
		_handler.RemoveCallbacksAndMessages(null);
		HideMenu();
		HideScaleUi();
		_current = null;
		_currentName = "";
	}

	/// <summary>宠物此刻在屏幕上的左上角(像素)。回家时交回小屋(原地接力)。
	/// 自己算:窗口坐标 + 可见区原点(见 RefreshFrame 的说明,dumpsys 实测 parent=[0,136][1080,2274],
	/// 可见区就是这一块)—— 不用 getLocationOnScreen:updateViewLayout 与系统 relayout 有时序差,
	/// 它报的可能是上一次的位置(实测回家时整整差了一个状态栏高,宠物就摆到了小屋偏下的地方)。</summary>
	public (int X, int Y) ScreenPosition()
	{
		if (_visibleRect.Width() > 0 && _visibleRect.Height() > 0)
			return (_winX + _visibleRect.Left, _winY + _visibleRect.Top);
		return (_winX, _winY); // 还没量到可见区(刚挂上窗口):至少给个窗口坐标
	}

	// ---- 自绘 ----

	protected override void OnDraw(Canvas canvas)
	{
		base.OnDraw(canvas);
		var bmp = _current;
		if (bmp is null)
			return;
		if (_facingRight)
		{
			// 素材只有朝左帧,朝右走时水平镜像(与面板侧 ScaleX=-1 同款)
			canvas.Save();
			canvas.Scale(-1f, 1f, Width / 2f, Height / 2f);
			canvas.DrawBitmap(bmp, 0, 0, null);
			canvas.Restore();
		}
		else
		{
			canvas.DrawBitmap(bmp, 0, 0, null);
		}
	}

	// ---- 触摸:拖拽(10px 阈值确认) / 双击呼出菜单 / 单击戳一下 ----

	public override bool OnTouchEvent(MotionEvent? e)
	{
		if (e is null)
			return false;
		switch (e.ActionMasked)
		{
			case MotionEventActions.Down:
				if (_menu is not null)
				{
					HideMenu(); // 菜单开着时点宠物 = 收起菜单
					return true;
				}
				_dragging = false;
				_downRawX = e.RawX;
				_downRawY = e.RawY;
				_downWinX = _winX;
				_downWinY = _winY;
				return true;

			case MotionEventActions.Move:
				var dx = e.RawX - _downRawX;
				var dy = e.RawY - _downRawY;
				if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > DragThresholdPx)
				{
					_dragging = true; // 超阈值才认作拖拽,手抖仍按点击处理
					_tapCount = 0;         // 拖拽不算连点,计数作废
					++_actionToken;        // 作废在途的动作复位,免得它中途把拖拽帧切回去
					_sleeping = false;     // 拖拽唤醒(与桌面端 Mood.Exit 同款)
					ShowFrame("pet_drag"); // 拎起来
				}
				if (_dragging)
					MoveTo(_downWinX + (int)dx, _downWinY + (int)dy);
				return true;

			case MotionEventActions.Up:
				if (_dragging)
				{
					// 拖拽结束:引擎对齐到新位置,从那继续走;画面回走动/常态帧
					_walker.Restore(_winX, _facingRight);
					ShowFrame(_walking ? "pet_left" : "pet");
				}
				else
				{
					OnTap();
				}
				_dragging = false;
				return true;

			case MotionEventActions.Cancel:
				if (_dragging)
					ShowFrame(_walking ? "pet_left" : "pet"); // 手势被系统打断,同样算放下
				_dragging = false;
				return true;
		}
		return base.OnTouchEvent(e);
	}

	/// <summary>
	/// 一次点击(非拖拽)。窗口内连点两下 = 呼出菜单,否则戳一下(1 秒 Clicked 帧,同面板侧)。
	/// 单击不做延迟等待:戳的反馈立即给,第二下再补一次计数命中就弹菜单。
	/// </summary>
	private void OnTap()
	{
		var now = SystemClock.UptimeMillis();
		if (now - _lastTapMs > DoubleTapWindowMs)
			_tapCount = 0; // 隔太久,重新数
		_lastTapMs = now;
		if (++_tapCount >= 2)
		{
			_tapCount = 0;
			ShowMenu();
			return;
		}
		PlayAction("pet_clicked", 1000);
	}

	private float _downRawX, _downRawY;
	private int _downWinX, _downWinY;

	private void MoveTo(int x, int y, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
	{
		var cx = Math.Clamp(x, _minX, _maxX);
		var cy = Math.Clamp(y, _minY, _maxY);
		if (cx == _winX && cy == _winY)
			return;
		if (cy != _winY)
			Android.Util.Log.Info("QPET", $"位置[{caller}] y {_winY} → {cy} (要求 {y}, 范围 [{_minY},{_maxY}])");
		_winX = cx;
		_winY = cy;
		if (_layout is null || _windowManager is null)
			return;
		_layout.X = cx;
		_layout.Y = cy;
		try { _windowManager.UpdateViewLayout(this, _layout); } catch { }
	}

	/// <summary>
	/// 解析宠物的可拖范围。窗口坐标不是屏幕坐标:系统把悬浮窗约束在系统栏之间
	/// (dumpsys 实测 parent=[0,136][1080,2274],而屏幕是 1080x2400 —— 136 就是状态栏高度),
	/// 拿全屏高度当上界会让宠物一路拖到导航栏底下(实测只剩 3px 可见,触摸点已在屏幕外,
	/// 用户再也点不中,只能回面板用"回家"收回)。
	/// 悬浮窗又不会收到 insets 派发(覆写 onApplyWindowInsets 连 RequestApplyInsets 都没回调),
	/// 所以取系统给的"窗口可见区域"的**尺寸**当上界 —— 窗口坐标系的原点就是可见区左上角,
	/// 两者尺寸相同,不需要去反推偏移(试过用 设的X/Y 与 getLocationOnScreen 反推,但
	/// updateViewLayout 与系统 relayout 有时序差,偏移会越算越偏、宠物一路飞出屏幕)。
	/// </summary>
	private void RefreshFrame()
	{
		try
		{
			GetWindowVisibleDisplayFrame(_visibleRect);
			if (_visibleRect.Width() <= 0 || _visibleRect.Height() <= 0)
				return;

			_minX = 0;
			_minY = 0;
			_maxX = Math.Max(0, _visibleRect.Width() - _petSize);
			_maxY = Math.Max(0, _visibleRect.Height() - _petSize);
			_walker.Min = _minX;   // 横屏/侧边手势栏下横向范围也会变
			_walker.Max = _maxX;

			if (!_frameLogged)
			{
				_frameLogged = true;
				Android.Util.Log.Info("QPET", $"可见区域={_visibleRect} → 可拖 x[{_minX},{_maxX}] y[{_minY},{_maxY}]; 屏幕 {_screenW}x{_screenH} 宠物 {_petSize}");
			}

			// 出门落点:面板给的是屏幕坐标,窗口坐标要从可见区左上角算起。
			// 拿**面板给的原始屏幕坐标**算,不要对已经摆过的 _winX/_winY 做减法 ——
			// 那样每多一次换算就多叠一份系统栏高度,位置就"一直在变"了。
			if (_pendingStartPos)
			{
				_pendingStartPos = false;
				MoveTo(_startScreenX - _visibleRect.Left, _startScreenY - _visibleRect.Top); // 内部 clamp 并 relayout
				_walker.Restore(_winX, _facingRight);        // 走动从落点接着来(点"走路"时不跳回原位)
				if (_layout is not null && _layout.Alpha < 1f)
				{
					_layout.Alpha = 1f; // 落点就位了,现身(见 CreateLayoutParams)
					try { _windowManager?.UpdateViewLayout(this, _layout); } catch { }
				}
				var (px, py) = ScreenPosition();
				Android.Util.Log.Info("QPET", $"落点:面板给 ({_startScreenX},{_startScreenY}) → 窗口 ({_winX},{_winY}) 屏幕 ({px},{py}) 可见区 {_visibleRect} 宠物 {_petSize}");
			}

			// 范围收窄(切系统栏/旋转)时把宠物拉回可见区 —— 停步状态下它自己不会走回来
			if (_winX > _maxX || _winX < _minX || _winY > _maxY || _winY < _minY)
				MoveTo(_winX, _winY);
		}
		catch (Exception ex)
		{
			Android.Util.Log.Error("QPET", $"RefreshFrame 异常: {ex}");
		}
	}

	/// <summary>首次显示/被系统重新布局后校正可拖范围。宠物走动时每帧都会走一次 relayout,
	/// 而尺寸不变时可见区域不会变,所以按秒节流,免得每秒白问系统 60 次。</summary>
	protected override void OnLayout(bool changed, int l, int t, int r, int b)
	{
		base.OnLayout(changed, l, t, r, b);
		var now = SystemClock.UptimeMillis();
		if (!changed && now - _lastFrameCheckMs < 1000)
			return;
		_lastFrameCheckMs = now;
		RefreshFrame();
	}

	// ---- 走动与眨眼 ----

	private void ScheduleTick()
	{
		if (_stopped)
			return;
		_handler.PostDelayed(_tickAction, FrameMs);
	}

	private long _lastTickMs = SystemClock.UptimeMillis();

	private void Tick()
	{
		if (_stopped)
			return;
		var now = SystemClock.UptimeMillis();
		var delta = TimeSpan.FromMilliseconds(now - _lastTickMs);
		_lastTickMs = now;

		// 动作帧播放期间站住(与桌面端"动作态暂停走动"一致):否则下面这行走动帧
		// 会在下一帧把刚点的比心/跪下/吃面盖掉,看着像按钮没反应
		if (_walking && !IsActionFrame(_currentName) && !_dragging && !_sleeping && _menu is null && _scaleUi is null)
		{
			_walker.Update(delta);
			MoveTo((int)Math.Round(_walker.Position), _winY);
			if (_currentName != "pet_left")
				ShowFrame("pet_left"); // 走动帧
		}
		ScheduleTick();
	}

	private void StartBlink()
	{
		if (_stopped)
			return;
		_handler.PostDelayed(_blinkAction, _rand.Next(BlinkDelayRange[0], BlinkDelayRange[1]));
	}

	private void BlinkTick()
	{
		if (_stopped)
			return;
		// 只在常态帧上眨(Walk/动作/睡觉期间不打扰)
		if (_currentName == "pet")
		{
			ShowFrame("pet_blink_half");
			_handler.PostDelayed(() => ShowFrame("pet_blink_closed"), 90);
			_handler.PostDelayed(() => ShowFrame("pet_blink_half"), 180);
			_handler.PostDelayed(() => ShowFrame("pet"), 270);
		}
		StartBlink();
	}

	// ---- 动作 ----

	/// <summary>播放一次性动作帧,到时复位回常态。</summary>
	private void PlayAction(string frameName, int durationMs)
	{
		ShowFrame(frameName);
		var token = ++_actionToken;
		_handler.PostDelayed(() =>
		{
			if (_stopped || token != _actionToken)
				return;
			if (!_sleeping)
				ShowFrame(_walking ? "pet_left" : "pet");
		}, durationMs);
	}

	/// <summary>
	/// 一次性动作帧(比心/跪下/吃面/被戳)。播放期间必须站住:走路开着时 Tick 每帧都会把画面
	/// 切回走动帧,动作帧活不过一帧 —— 表现就是"菜单里点比心/跪下/吃面没任何反应"。
	/// 睡觉与拖拽各自有状态旗标,不在此列。
	/// </summary>
	private static bool IsActionFrame(string name) =>
		name is "pet_heart" or "pet_kneel" or "pet_eat" or "pet_clicked";

	/// <summary>值得记日志的低频帧(用户操作触发);眨眼/走动的切换不记,免得刷屏。</summary>
	private static bool IsNotableFrame(string name) =>
		IsActionFrame(name) || name is "pet_drag" or "pet_sleep";

	private void ToggleSleep()
	{
		_sleeping = !_sleeping;
		++_actionToken; // 作废在途的动作复位
		ShowFrame(_sleeping ? "pet_sleep" : (_walking ? "pet_left" : "pet"));
	}

	private void ToggleWalk()
	{
		_walking = !_walking;
		++_actionToken;
		_sleeping = false;
		Android.Util.Log.Info("QPET", $"走路 → {(_walking ? "开始" : "停下")} (位置 ({_winX},{_winY}))");
		ShowFrame(_walking ? "pet_left" : "pet");
		if (_walking)
			_walker.Restore(_winX, _facingRight); // 从当前位置继续走,不跳回旧点
	}

	// ---- 呼出菜单(双击宠物弹出) ----

	private void ShowMenu()
	{
		if (_menu is not null || _windowManager is null)
			return;

		var menu = new LinearLayout(_context) { Orientation = Orientation.Vertical };
		var bg = new GradientDrawable();
		bg.SetColor(AndroidColor.ParseColor("#FFFDF7"));
		bg.SetCornerRadius(Dp(14));
		bg.SetStroke(Dp(1), AndroidColor.ParseColor("#F0E0CC"));
		menu.Background = bg;
		menu.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));

		// 与小屋呼出菜单(PetPanel.UpdateMenuButtons)逐项同序同字:比心/跪下/吃面/睡觉/走路/缩放,
		// 只是最后一项"外出"在这儿天然反过来 —— 已经在外面了,所以是"回家"。
		AddMenuButton(menu, "❤ 比心", () => { HideMenu(); PlayAction("pet_heart", 1000); });
		AddMenuButton(menu, "🙏 跪下", () => { HideMenu(); PlayAction("pet_kneel", 3000); });
		AddMenuButton(menu, "🍜 吃面", () => { HideMenu(); PlayAction("pet_eat", 3000); });
		AddMenuButton(menu, _sleeping ? "😴 醒来" : "😴 睡觉", () => { HideMenu(); ToggleSleep(); });
		AddMenuButton(menu, _walking ? "⏹ 停下" : "🚶 走路", () => { HideMenu(); ToggleWalk(); });
		AddMenuButton(menu, _scaling ? "✖ 取消缩放" : "🔍 缩放", () => { HideMenu(); ToggleScale(); });
		AddMenuButton(menu, "🏠 回家", () => { HideMenu(); _onGoHome(); });

		var type = OperatingSystem.IsAndroidVersionAtLeast(26)
			? WindowManagerTypes.ApplicationOverlay
			: WindowManagerTypes.Phone;
		var lp = new WindowManagerLayoutParams(
			WindowManagerLayoutParams.WrapContent, WindowManagerLayoutParams.WrapContent, type,
			WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
			Format.Translucent)
		{
			Gravity = GravityFlags.Top | GravityFlags.Left, // 自己定位:贴着宠物放,别压在它身上
		};
		menu.Visibility = ViewStates.Invisible; // 定位前不露脸,免得先在左上角闪一下
		try
		{
			_windowManager.AddView(menu, lp);
			_menu = menu;
			// 窗口是 WrapContent,尺寸要等布局跑完才算得出来,所以延后一拍:量到了再挪到宠物旁边并现身
			menu.Post(() =>
			{
				if (_menu != menu || _stopped)
					return; // 期间已被收起/销毁
				var (mx, my) = PlaceMenuClear(_winX, _winY, _petSize, menu.Width, menu.Height);
				lp.X = mx;
				lp.Y = my;
				try { _windowManager.UpdateViewLayout(menu, lp); } catch { }
				menu.Visibility = ViewStates.Visible;
				Android.Util.Log.Info("QPET", $"菜单 → ({mx},{my}) {menu.Width}x{menu.Height}; 宠物 ({_winX},{_winY}) {_petSize} 屏幕 ({ScreenPosition().X},{ScreenPosition().Y})");
				// 按钮在屏幕上的实际矩形(挪完窗口要等系统 relayout,所以再延一拍才是准的)。
				// 留着给自动化测试点准位置,也方便核对菜单有没有压住宠物。
				menu.Post(() =>
				{
					if (_menu != menu || _stopped)
						return;
					var loc = new int[2];
					var sb = new System.Text.StringBuilder();
					for (var i = 0; i < menu.ChildCount; i++)
					{
						if (menu.GetChildAt(i) is not Android.Views.View child)
							continue;
						child.GetLocationOnScreen(loc);
						sb.Append($"[{(child as Android.Widget.TextView)?.Text}@{loc[0]},{loc[1]} {child.Width}x{child.Height}]");
					}
					Android.Util.Log.Info("QPET", $"菜单按钮: {sb}");
				});
			});
		}
		catch (Exception ex)
		{
			_menu = null;
			Android.Util.Log.Error("QPET", $"菜单 AddView 失败: {ex}");
		}
	}

	/// <summary>
	/// 给菜单挑个不压在宠物身上的位置:优先宠物右侧,其次左侧,再不行上方/下方;
	/// 四个方向都挤不下(宠物贴边 + 菜单比空隙还大)就退回右侧贴边 —— 宁可压一点,也别把菜单丢出屏幕。
	/// 坐标是窗口坐标(与 _winX/_winY 同一坐标系:原点 = 可见区左上角)。
	/// </summary>
	private (int X, int Y) PlaceMenuClear(int petX, int petY, int petSize, int menuW, int menuH)
	{
		var areaW = _visibleRect.Width() > 0 ? _visibleRect.Width() : _screenW;
		var areaH = _visibleRect.Height() > 0 ? _visibleRect.Height() : _screenH;
		var centerY = petY + petSize / 2;

		int ClampX(int v) => Math.Clamp(v, 0, Math.Max(0, areaW - menuW));
		int ClampY(int v) => Math.Clamp(v, 0, Math.Max(0, areaH - menuH));

		var candidates = new (int X, int Y)[]
		{
			(petX + petSize + MenuGapPx, centerY - menuH / 2),            // 右
			(petX - menuW - MenuGapPx, centerY - menuH / 2),              // 左
			(petX + (petSize - menuW) / 2, petY - menuH - MenuGapPx),     // 上
			(petX + (petSize - menuW) / 2, petY + petSize + MenuGapPx),   // 下
		};
		foreach (var (x, y) in candidates)
		{
			var cx = ClampX(x);
			var cy = ClampY(y);
			if (!OverlapsPet(cx, cy, menuW, menuH, petX, petY, petSize))
				return (cx, cy);
		}
		return (ClampX(petX + petSize + MenuGapPx), ClampY(centerY - menuH / 2));
	}

	private static bool OverlapsPet(int x, int y, int w, int h, int petX, int petY, int petSize)
		=> x < petX + petSize && x + w > petX && y < petY + petSize && y + h > petY;

	private void HideMenu()
	{
		if (_menu is null)
			return;
		try { _windowManager?.RemoveView(_menu); } catch { }
		_menu.Dispose();
		_menu = null;
		_tapCount = 0; // 收起后重新数
	}

	// ---- 缩放(与面板 ScaleUi 同款:80-400 滑块实时预览,确定 = 持久化 + 上传云端,取消 = 还原) ----

	private const double ScaleMin = 80;
	private const double ScaleMax = 400;

	private void ToggleScale()
	{
		if (_scaling)
		{
			CancelScale();
			return;
		}
		if (_scaleUi is not null || _windowManager is null)
			return;
		_scaling = true;
		_petDpBase = _petDp;
		++_actionToken;   // 作废在途的动作复位(与面板 ExitMood 同款:进缩放先站住)
		_sleeping = false;
		ShowFrame(_walking ? "pet_left" : "pet");
		BuildScaleUi();
	}

	/// <summary>缩放面板:提示 + 滑块 + 尺寸文字/取消/确定(桌面 ScalePanel 同款,只换成系统层的窗口)。</summary>
	private void BuildScaleUi()
	{
		var panel = new LinearLayout(_context) { Orientation = Orientation.Vertical };
		var bg = new GradientDrawable();
		bg.SetColor(AndroidColor.ParseColor("#FFFDF7"));
		bg.SetCornerRadius(Dp(12));
		bg.SetStroke(Dp(1), AndroidColor.ParseColor("#F0E0CC"));
		panel.Background = bg;
		panel.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));

		var hint = new TextView(_context) { Text = "拖动滑块调整大小" };
		hint.SetTextColor(AndroidColor.ParseColor("#B08A62"));
		hint.SetTextSize(Android.Util.ComplexUnitType.Sp, 11);
		hint.Gravity = GravityFlags.CenterHorizontal;
		panel.AddView(hint);

		var slider = new SeekBar(_context) { Max = (int)(ScaleMax - ScaleMin) };
		slider.Progress = (int)Math.Round(_petDp - ScaleMin);
		slider.ProgressChanged += (_, e) =>
		{
			if (!e.FromUser)
				return; // 程序回写不算手动拖动(与面板 _syncingSlider 同款,防回环)
			_petDp = ScaleMin + e.Progress;
			ApplyPetSize();
		};
		panel.AddView(slider);

		var row = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
		row.SetGravity(GravityFlags.CenterHorizontal);
		_scaleText = new TextView(_context) { Text = $"{_petDp:F0} px" };
		_scaleText.SetTextColor(AndroidColor.ParseColor("#C4702F"));
		_scaleText.SetTextSize(Android.Util.ComplexUnitType.Sp, 13);
		_scaleText.SetPadding(0, 0, Dp(10), 0);
		_scaleText.Gravity = GravityFlags.CenterVertical;
		row.AddView(_scaleText);
		row.AddView(ScaleButton("确定", "#FF8C42", "#FFFFFF", ConfirmScale));
		row.AddView(ScaleButton("取消", "#FCE8E2", "#E05B4C", CancelScale));
		panel.AddView(row);

		var type = OperatingSystem.IsAndroidVersionAtLeast(26)
			? WindowManagerTypes.ApplicationOverlay
			: WindowManagerTypes.Phone;
		var lp = new WindowManagerLayoutParams(
			Dp(280), WindowManagerLayoutParams.WrapContent, type,
			WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
			Format.Translucent)
		{
			Gravity = GravityFlags.Top | GravityFlags.Left, // 同菜单:自己摆,别压在宠物身上
		};
		panel.Visibility = ViewStates.Invisible; // 量到尺寸再现身,免得在左上角闪一下
		try
		{
			_windowManager!.AddView(panel, lp);
			_scaleUi = panel;
			panel.Post(() =>
			{
				if (_scaleUi != panel || _stopped)
					return;
				var (mx, my) = PlaceMenuClear(_winX, _winY, _petSize, panel.Width, panel.Height);
				lp.X = mx;
				lp.Y = my;
				try { _windowManager.UpdateViewLayout(panel, lp); } catch { }
				panel.Visibility = ViewStates.Visible;
			});
		}
		catch (Exception ex)
		{
			_scaleUi = null;
			_scaling = false;
			Android.Util.Log.Error("QPET", $"缩放面板 AddView 失败: {ex}");
		}
	}

	private TextView ScaleButton(string text, string bgColor, string fgColor, Action onClick)
	{
		var tv = new TextView(_context) { Text = text, Clickable = true };
		var bg = new GradientDrawable();
		bg.SetColor(AndroidColor.ParseColor(bgColor));
		bg.SetCornerRadius(Dp(15));
		tv.Background = bg;
		tv.SetTextColor(AndroidColor.ParseColor(fgColor));
		tv.SetTextSize(Android.Util.ComplexUnitType.Sp, 12);
		tv.SetPadding(Dp(16), Dp(6), Dp(16), Dp(6));
		tv.Click += (_, _) => onClick();
		return tv;
	}

	/// <summary>滑块实时套用尺寸:重解码素材帧 + 改窗口边长 + 收回越界位置(面板 ApplyPetSize 同款)。</summary>
	private void ApplyPetSize()
	{
		if (_scaleText is not null)
			_scaleText.Text = $"{_petDp:F0} px";
		var px = PetSizeFromDp(_petDp);
		if (px == _petSize)
			return;
		_petSize = px;
		RebuildFrames();
		if (_layout is not null && _windowManager is not null)
		{
			_layout.Width = _petSize;
			_layout.Height = _petSize;
			try { _windowManager.UpdateViewLayout(this, _layout); } catch { }
		}
		RefreshFrame();
		MoveTo(_winX, _winY); // 尺寸变大防越界(MoveTo 内部按新范围 clamp)
	}

	/// <summary>换尺寸后素材帧全部作废重解码(帧是按旧边长缩放的,不重建会拉伸糊掉)。</summary>
	private void RebuildFrames()
	{
		var keep = _currentName;
		_current = null;   // 先摘掉引用再回收:画布正在用的位图不能回收
		_currentName = "";
		foreach (var bmp in _frames.Values)
			bmp.Recycle();
		_frames.Clear();
		ShowFrame(string.IsNullOrEmpty(keep) ? "pet" : keep);
	}

	private void CancelScale()
	{
		_petDp = _petDpBase;
		ApplyPetSize();
		HideScaleUi();
	}

	/// <summary>确定缩放:存配置并持久化,上传云端(与面板 OnScaleOkClicked 同款)。</summary>
	private void ConfirmScale()
	{
		PetConfig.PetSize = _petDp;
		PetConfig.Save(AppState.Settings); // 本地持久化,下次开悬浮窗生效
		if (AppState.Client.IsConnected)
			AppState.Client.UpdatePetSize(_petDp); // 云端:同账号多端同步(含小屋面板)
		HideScaleUi();
	}

	private void HideScaleUi()
	{
		_scaling = false;
		if (_scaleUi is not null)
		{
			try { _windowManager?.RemoveView(_scaleUi); } catch { }
			_scaleUi.Dispose();
			_scaleUi = null;
		}
		_scaleText = null;
		_walker.Restore(_winX, _facingRight); // 走动暂停期间位置可能被尺寸变化挪过,从当下接着走
	}

	private void AddMenuButton(LinearLayout menu, string text, Action onClick)
	{
		var tv = new TextView(_context)
		{
			Text = text,
			Clickable = true,
		};
		tv.SetTextColor(AndroidColor.ParseColor("#5C432C"));
		tv.SetTextSize(Android.Util.ComplexUnitType.Sp, 15);
		tv.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));
		tv.Click += (_, _) =>
		{
			Android.Util.Log.Info("QPET", $"菜单点击:「{text}」");
			onClick();
		};
		menu.AddView(tv);
	}

	// ---- 帧加载 ----

	private void ShowFrame(string name)
	{
		if (_currentName == name)
			return;
		var bmp = GetFrame(name);
		if (bmp is null)
		{
			Android.Util.Log.Warn("QPET", $"宠物帧 {name} 素材缺失(动作会没有任何反应)");
			return;
		}
		_current = bmp;
		_currentName = name;
		if (IsNotableFrame(name))
			Android.Util.Log.Info("QPET", $"宠物帧 → {name}"); // 低频帧留痕,便于无识图排查"点了没反应"
		Invalidate();
	}

	private Bitmap? GetFrame(string name)
	{
		if (_frames.TryGetValue(name, out var cached))
			return cached;
		var res = _context.Resources;
		var id = res?.GetIdentifier(name, "drawable", _context.PackageName) ?? 0;
		if (id == 0)
			return null;

		// 素材 PNG 是 2048²,且落在无密度后缀的 drawable/(系统按 mdpi 算):默认解码会按屏幕密度
		// 放大(xxhdpi 上 → 6144²,几十 MB,老机型直接 OOM)。InScaled=false 关掉密度缩放,
		// InSampleSize 按 2 的幂直接降到目标尺寸附近,再精缩到宠物大小。
		var opts = new BitmapFactory.Options { InScaled = false, InJustDecodeBounds = true };
		BitmapFactory.DecodeResource(res, id, opts);
		var sample = 1;
		while (opts.OutWidth > 0 && opts.OutWidth / (sample * 2) >= _petSize)
			sample *= 2;
		opts.InJustDecodeBounds = false;
		opts.InSampleSize = sample;

		var raw = BitmapFactory.DecodeResource(res, id, opts);
		if (raw is null)
			return null;
		var scaled = Bitmap.CreateScaledBitmap(raw, _petSize, _petSize, true);
		if (!ReferenceEquals(scaled, raw))
			raw.Recycle();
		_frames[name] = scaled;
		return scaled;
	}

	private float Density => _context.Resources?.DisplayMetrics?.Density ?? 1f;

	private int Dp(double dp) => (int)Math.Round(dp * Density);

	/// <summary>
	/// 面板侧的尺寸走 MAUI 的 WidthRequest(单位 dip,由 MAUI 乘密度),悬浮窗这边是裸像素:
	/// 同一个 PetConfig.PetSize 必须自己乘一次密度,否则悬浮窗那只只有小屋里的 1/密度 大
	/// (420dpi 上差 2.6 倍,肉眼一看就是"屋里屋外不一样大")。
	/// </summary>
	private int PetSizeFromDp(double dp) => Math.Max(Dp(48), (int)Math.Round(dp * Density));
}
