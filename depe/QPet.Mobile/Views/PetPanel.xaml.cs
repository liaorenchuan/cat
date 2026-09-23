using System.Diagnostics;
using QPet.Core;
using QPet.Mobile.Services;

namespace QPet.Mobile.Views;

/// <summary>
/// 宠物小屋(对齐桌面 PetHouseView):素材宠物在活动区站住眨眼 / 自由走动 / 做动作。
/// 走动由 QPet.Core.PetWalker 引擎驱动(掉头触发朝向帧切换与随机垂直目标),窗口 DispatcherTimer
/// 当渲染帧;情绪状态机 + 自然眨眼 = 桌面 PetMoodController/BlinkLoop 语义(限时动作自动回常态,
/// 睡觉/走路为开关态);点宠物 = 戳一下(睡觉中被戳会醒)。
/// 缩放宽 80-400(桌面同款):确定 = PetConfig 持久化 + UpdatePetSize 上传云端(登录下发 /
/// 他端修改回推经 AppState.CloudPetSize 应用,正在手动缩放时不打扰);本地随时保存。
/// 外出(桌面 PetFloatingWindow 的移动端形态):把宠物交给系统悬浮窗前台服务(其他应用上层),
/// 面板内宠物隐藏让位;悬浮窗菜单"回家"经 PetOverlayService.GoHomeRequested 通知面板回位。
/// </summary>
public partial class PetPanel : ContentView
{
	// 素材帧(Resources/Images,文件名 = 引用名)
	private const string ImgNormal = "pet.png";
	private const string ImgBlinkHalf = "pet_blink_half.png";
	private const string ImgBlinkClosed = "pet_blink_closed.png";
	private const string ImgWalk = "pet_left.png";   // 画面朝左,朝右走水平镜像
	private const string ImgDrag = "pet_drag.png";   // 被拎起来(拖拽中)

	// ---- 走动(桌面 PetWalkDriver 同构:引擎 + 时钟,定时器当渲染帧) ----
	private readonly PetWalker _walker = new() { Speed = PetConfig.WalkSpeed };
	private readonly Stopwatch _walkClock = new();
	private TimeSpan _walkLastTick;
	private IDispatcherTimer? _walkTimer;
	private readonly Random _rand = new();
	private double _targetY;    // 垂直行走目标(水平掉头随机新目标,走动中上下慢移,可到活动区四角)
	private bool _everCentered; // 布局就绪后宠物摆到活动区中央(此后走动接续,不再回中)

	// ---- 情绪 + 眨眼(桌面 PetMoodController/BlinkLoop 语义,单视图自包含) ----
	private PetMood _mood = PetMood.Normal;
	private CancellationTokenSource _cts = new();
	private Task? _blinkTask;

	// ---- 缩放(桌面 OnScale/OnScaleOk 同构) ----
	private double _petSize;       // 当前宠物尺寸(缩放中实时变化)
	private double _petSizeBase;   // 进缩放模式时的尺寸(取消时还原)
	private bool _scaling;
	private bool _syncingSlider;   // 程序回写滑块中(避免 ValueChanged 回环)

	// ---- 外出(系统悬浮窗) ----
	private bool _outside;         // 宠物在悬浮窗上:面板内隐藏让位,走动/眨眼停摆

	// ---- 呼出菜单(双击宠物弹出;桌面右键菜单的移动端形态,不再摆成一排按钮) ----
	private const int DoubleTapWindowMs = 400; // 相邻两击的最大间隔(超时重新数;双击窗口比三击短,免得"点一下隔会儿再点一下"误触)
	private const double MenuGapPx = 12;       // 菜单与宠物之间的空隙
	private int _tapCount;
	private long _lastTapMs;
	private bool _menuPausedWalk;  // 菜单开着时走动暂停,收起后接着走
	private int _menuPlaceTries;   // 菜单定位重试(尺寸要等布局跑完才量得到)

	// ---- 拖拽(手势在活动区上;超 10px 才算拖,拖起切拖拽帧,松手从落点接着走) ----
	private const double DragThresholdPx = 10; // 拖拽确认阈值(照搬桌面 PetDragTracker)
	private bool _panning;         // 已越过阈值,真的在拖了
	private double _panStartX, _panStartY;     // 起手时的宠物位置(拖拽基准)
	private bool _panWasWalking;   // 拖之前在走路:松手后接着走

	bool RoomReady => PetArea.Width > 0 && PetArea.Height > 0;

	public PetPanel()
	{
		InitializeComponent();
		_petSize = PetConfig.PetSize;
		_walker.Speed = PetConfig.WalkSpeed;
		_walker.DirectionChanged += OnWalkerFacingChanged; // 掉头:切镜像帧 + 随机新垂直目标
		PetArea.SizeChanged += OnPetAreaResized;
		PropertyChanged += OnPanelPropertyChanged; // 切页签可见性(MAUI 无 IsVisibleChanged 事件)
		AppState.CloudPetSize += OnCloudPetSize; // 云端尺寸(登录下发/他端改):配置已持久化,这里应用画面
		PetOverlayService.GoHomeRequested -= OnOverlayGoHome;
		PetOverlayService.GoHomeRequested += OnOverlayGoHome; // 悬浮窗菜单"回家":面板宠物回位(静态事件,先减后加防重复订阅)
	}

	// ---- 可见性 / 布局 ----

	void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(IsVisible))
			OnPanelVisibleChanged(sender, EventArgs.Empty);
	}

	void OnPanelVisibleChanged(object? sender, EventArgs e)
	{
		if (!IsVisible)
		{
			// 切走页签:停走动与眨眼;缩放模式未确认的尺寸还原退出(桌面 OnUnloaded 同)
			StopWalkTimer();
			CancelTimers();
			HidePetMenu(); // 菜单是浮层,切走页签要收掉,免得回来时莫名开着
			_tapCount = 0;
			if (_scaling)
			{
				_petSize = _petSizeBase;
				ApplyPetSize();
				ExitScaleMode();
			}
			return;
		}
		// 切走页签期间悬浮窗可能被系统回收 / 从通知栏停掉:回来以服务的真实状态校正画面
		if (_outside != PetOverlay.IsRunning)
			SetOutState(PetOverlay.IsRunning);
		if (_outside)
			return; // 宠物在系统层玩耍:面板内不摆位、不走动、不眨眼
		if (_mood == PetMood.Walk)
			StartWalkTimer();
		else if (_mood == PetMood.Normal)
			StartBlink();
		// 刚布局完(几何可能还没就位):延一拍摆位/走位。
		// 未摆过位时先隐形:首次进入布局就绪前 Image 按默认尺寸画在活动区左上角,
		// 摆位(居中+设尺寸)在下一拍才执行,先显错位帧再瞬移 = "闪一下"的根源。
		if (!_everCentered)
			PetImage.Opacity = 0;
		Dispatcher.Dispatch(RefreshLayout);
	}

	/// <summary>活动区可用后:算走动范围,把宠物摆到当前位(首次回中央),走动中则接续启动。</summary>
	void RefreshLayout()
	{
		if (!IsVisible || !RoomReady)
			return;
		UpdateWalkRange();
		ApplyPetSize(); // 宠物宽高跟着尺寸定(云端尺寸切页签回来也生效)
		if (!_everCentered)
		{
			_everCentered = true;
			if (_mood != PetMood.Walk)
				CenterPet();
		}
		ClampPetPosition();
		if (_mood == PetMood.Walk && _walkTimer is not { IsRunning: true })
			StartWalkTimer();
		// 摆位完成:同一帧恢复可见(隐形只覆盖等几何就绪的那一两帧,不留中间错位帧)
		if (PetImage.Opacity < 1)
			PetImage.Opacity = 1;
	}

	void OnPetAreaResized(object? sender, EventArgs e)
	{
		if (IsVisible)
			Dispatcher.Dispatch(RefreshLayout);
	}

	/// <summary>可走范围 = 活动区四角(上下左右贴边,宠物边角可达,桌面同常数 8/12)。</summary>
	void UpdateWalkRange()
	{
		_walker.Min = 8;
		_walker.Max = Math.Max(_walker.Min, PetArea.Width - _petSize - 12);
		_walker.MinY = 12;
		_walker.MaxY = Math.Max(_walker.MinY, PetArea.Height - _petSize - 12);
	}

	void ClampPetPosition()
	{
		PetImage.TranslationX = Math.Clamp(PetImage.TranslationX, _walker.Min, _walker.Max);
		_targetY = Math.Clamp(_targetY, _walker.MinY, _walker.MaxY);
		PetImage.TranslationY = Math.Clamp(PetImage.TranslationY, _walker.MinY, _walker.MaxY);
	}

	/// <summary>常态停位:宠物与垂直目标都回活动区中央(桌面 Loaded 同款)。</summary>
	void CenterPet()
	{
		var x = (_walker.Min + _walker.Max) / 2;
		var y = (_walker.MinY + _walker.MaxY) / 2;
		_walker.Position = x;
		PetImage.TranslationX = x;
		_targetY = y;
		PetImage.TranslationY = y;
	}

	// ---- 情绪(桌面 PetMoodController 同构:Enter 切状态,限时自动回常态,睡觉/走路常驻开关) ----

	void EnterMood(PetMood mood, TimeSpan? duration = null)
	{
		if (_mood != mood)
		{
			CancelTimers(); // 新状态先取消旧眨眼/旧限时计时,避免残留计时把新状态误唤醒
			_mood = mood;
			if (mood == PetMood.Walk)
			{
				// 走动:从当前坐标接续(桌面 onEnter 同:引擎位置对齐画面)
				_walker.Position = PetImage.TranslationX;
				SetWalkFrame();
				StartWalkTimer();
			}
			else
			{
				StopWalkTimer();
				PetImage.ScaleX = 1; // 退出走动:还原镜像
				if (mood != PetMood.Normal)
					PetImage.Source = FrameFor(mood);
			}
		}
		// 限时动作:有时长就(重新)计时,到时自动回常态(桌面 ResetAfterAsync 同)
		if (duration is { } d)
		{
			CancelTimers();
			var ct = _cts.Token;
			_ = ResetAfterAsync(d, ct);
		}
	}

	async Task ResetAfterAsync(TimeSpan duration, CancellationToken ct)
	{
		try
		{
			await Task.Delay(duration, ct);
		}
		catch (OperationCanceledException)
		{
			return; // 已被新状态取代
		}
		ExitMood();
	}

	void ExitMood()
	{
		if (_mood == PetMood.Normal)
			return;
		CancelTimers();
		_mood = PetMood.Normal;
		StopWalkTimer();
		PetImage.ScaleX = 1;
		PetImage.Source = ImgNormal;
		UpdateMenuButtons();
		if (IsVisible)
			StartBlink();
	}

	static string FrameFor(PetMood mood) => mood switch
	{
		PetMood.Heart => "pet_heart.png",
		PetMood.Kneel => "pet_kneel.png",
		PetMood.Eat => "pet_eat.png",
		PetMood.Clicked => "pet_clicked.png",
		PetMood.Sleep => "pet_sleep.png",
		_ => ImgNormal, // Normal/Walk 由常态与走动逻辑自己摆
	};

	// ---- 自然眨眼(桌面 BlinkLoop 同构:随机 2.5-6s 眨一次,半闭→全闭→半闭→睁,共约 270ms) ----

	void StartBlink()
	{
		if (!IsVisible || _mood != PetMood.Normal || _blinkTask is not null)
			return;
		_blinkTask = BlinkLoopAsync(_cts.Token);
	}

	async Task BlinkLoopAsync(CancellationToken ct)
	{
		var nextDelay = _rand.Next(2500, 6001);
		try
		{
			while (true)
			{
				await Task.Delay(nextDelay, ct);
				nextDelay = _rand.Next(2500, 6001);
				if (_mood != PetMood.Normal)
					return; // 状态已切(理论上 token 先取消,双保险)
				PetImage.Source = ImgBlinkHalf;
				await Task.Delay(90, ct);
				PetImage.Source = ImgBlinkClosed;
				await Task.Delay(90, ct);
				PetImage.Source = ImgBlinkHalf;
				await Task.Delay(90, ct);
				PetImage.Source = ImgNormal;
			}
		}
		catch (OperationCanceledException)
		{
			// 隐藏/切状态,正常退出
		}
	}

	// ---- 走动(桌面 PetWalkDriver + PetWalkFrames 同构) ----

	/// <summary>掉头:走动中切镜像帧并随机一个新的垂直目标(走动缓慢上下,可到活动区四角)。</summary>
	void OnWalkerFacingChanged(bool facingRight)
	{
		if (_mood != PetMood.Walk)
			return;
		SetWalkFrame();
		_targetY = _walker.MinY + _rand.NextDouble() * Math.Max(0, _walker.MaxY - _walker.MinY);
	}

	/// <summary>走动帧:素材 pet_left 画面朝左,朝右走水平镜像(桌面同:镜像帧由前端翻转)。</summary>
	void SetWalkFrame()
	{
		PetImage.Source = ImgWalk;
		PetImage.ScaleX = _walker.FacingRight ? -1 : 1;
	}

	void StartWalkTimer()
	{
		if (!IsVisible || !RoomReady || _walkTimer is { IsRunning: true })
			return;
		_walkClock.Restart();
		_walkLastTick = TimeSpan.Zero;
		_walkTimer ??= Dispatcher.CreateTimer();
		_walkTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60fps
		_walkTimer.Tick += OnWalkTick;
		_walkTimer.Start();
	}

	void StopWalkTimer()
	{
		if (_walkTimer is not null)
		{
			_walkTimer.Stop();
			_walkTimer.Tick -= OnWalkTick;
		}
		_walkClock.Reset();
	}

	void OnWalkTick(object? sender, EventArgs e)
	{
		var now = _walkClock.Elapsed;
		var delta = now - _walkLastTick;
		_walkLastTick = now;
		_walker.Update(delta);
		PetImage.TranslationX = _walker.Position;
		// 垂直:缓慢朝目标移动(掉头时随机新目标,桌面同款速率系数 0.5)
		var stepY = _walker.Speed * 0.5 * delta.TotalSeconds;
		var diff = _targetY - PetImage.TranslationY;
		PetImage.TranslationY += Math.Sign(diff) * Math.Min(Math.Abs(diff), stepY);
	}

	// ---- 呼出菜单(桌面右键菜单同款;睡觉/走路/缩放为开关态,文字随状态切换) ----

	/// <summary>弹出呼出菜单:走动先站住(与悬浮窗"菜单开着不走路"一致),收起后接着走。</summary>
	void ShowPetMenu()
	{
		if (PetMenu.IsVisible)
			return;
		if (_walkTimer is { IsRunning: true })
		{
			_menuPausedWalk = true;
			StopWalkTimer();
		}
		UpdateMenuButtons();
		PetMenu.IsVisible = true;
		MenuScrim.IsVisible = true;
		_menuPlaceTries = 0;
		Dispatcher.Dispatch(PositionPetMenu); // 下一拍菜单已布局,尺寸才量得到
	}

	/// <summary>
	/// 把菜单摆到宠物旁边(不压住它):优先右侧,其次左侧,再不行上方/下方。
	/// 菜单尺寸要等布局跑完才量得到(刚 IsVisible=true 时 Width 还是 -1),所以延后一拍、最多重试几次。
	/// </summary>
	void PositionPetMenu()
	{
		if (!PetMenu.IsVisible)
			return;
		var menuW = PetMenu.Width;
		var menuH = PetMenu.Height;
		if ((menuW <= 0 || menuH <= 0) && _menuPlaceTries++ < 5)
		{
			Dispatcher.Dispatch(PositionPetMenu);
			return;
		}
		if (menuW <= 0 || menuH <= 0)
			return; // 实在量不到:留在左上角也比卡在循环里强

		var (x, y) = PlaceMenuClear(PetImage.TranslationX, PetImage.TranslationY, _petSize,
			menuW, menuH, PetArea.Width, PetArea.Height);
		PetMenu.Margin = new Thickness(x, y, 0, 0);
	}

	/// <summary>
	/// 挑一个不与宠物矩形相交的菜单位置(活动区坐标系,与 PetImage 的 Translation 同一套)。
	/// 四个方向都挤不下(宠物贴边 + 菜单比空隙还大)就退回右侧贴边 —— 宁可压一点,也别把菜单丢到区外。
	/// </summary>
	static (double X, double Y) PlaceMenuClear(double petX, double petY, double petSize,
		double menuW, double menuH, double areaW, double areaH)
	{
		double ClampX(double v) => Math.Clamp(v, 0, Math.Max(0, areaW - menuW));
		double ClampY(double v) => Math.Clamp(v, 0, Math.Max(0, areaH - menuH));
		var centerY = petY + petSize / 2;

		var candidates = new (double X, double Y)[]
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

	static bool OverlapsPet(double x, double y, double w, double h, double petX, double petY, double petSize)
		=> x < petX + petSize && x + w > petX && y < petY + petSize && y + h > petY;

	/// <summary>收起呼出菜单(菜单里开的走路在收起后接着走)。</summary>
	void HidePetMenu()
	{
		if (!PetMenu.IsVisible)
			return;
		PetMenu.IsVisible = false;
		MenuScrim.IsVisible = false;
		if (!_menuPausedWalk)
			return;
		_menuPausedWalk = false;
		if (_mood == PetMood.Walk && IsVisible && !_outside)
			StartWalkTimer();
	}

	void OnMenuScrimTapped(object? sender, TappedEventArgs e) => HidePetMenu();

	void UpdateMenuButtons()
	{
		MenuSleepBtn.Text = _mood == PetMood.Sleep ? "😴 醒来" : "😴 睡觉";
		MenuWalkBtn.Text = _mood == PetMood.Walk ? "⏹ 停下" : "🚶 走路";
		MenuScaleBtn.Text = _scaling ? "✖ 取消缩放" : "🔍 缩放";
		MenuOutBtn.Text = _outside ? "🏠 回家" : "🚀 外出";
	}

	void OnHeartClicked(object? sender, EventArgs e)
	{
		HidePetMenu();
		EnterMood(PetMood.Heart, TimeSpan.FromSeconds(1));
	}

	void OnKneelClicked(object? sender, EventArgs e)
	{
		HidePetMenu();
		EnterMood(PetMood.Kneel, TimeSpan.FromSeconds(3));
	}

	void OnEatClicked(object? sender, EventArgs e)
	{
		HidePetMenu();
		EnterMood(PetMood.Eat, TimeSpan.FromSeconds(3));
	}

	void OnSleepClicked(object? sender, EventArgs e)
	{
		HidePetMenu();
		if (_mood == PetMood.Sleep)
			ExitMood(); // 再点一次唤醒
		else
			EnterMood(PetMood.Sleep);
		UpdateMenuButtons();
	}

	void OnWalkClicked(object? sender, EventArgs e)
	{
		HidePetMenu();
		if (_mood == PetMood.Walk)
		{
			ExitMood(); // 走路中再点:停步回常态
			return;
		}
		UpdateWalkRange(); // 走动范围按当前尺寸
		EnterMood(PetMood.Walk);
		UpdateMenuButtons();
	}

	/// <summary>
	/// 点宠物:单击戳一下(1 秒 Clicked 帧,桌面同;睡觉中被戳自动醒);
	/// 窗口内连点两下 = 弹出呼出菜单。单击不做延迟等待 —— 戳的反馈立即给,
	/// 第二下再补一次计数命中就弹菜单(第一下的戳帧会被菜单盖住,看着就是"双击开菜单")。
	/// </summary>
	void OnPetTapped(object? sender, TappedEventArgs e)
	{
		var pos = e.GetPosition(PetArea);
		Android.Util.Log.Info("QPET", $"点宠物: 坐标 {(pos is null ? "拿不到" : $"{pos.Value.X:0.#},{pos.Value.Y:0.#}")} 宠物矩形 [{PetImage.TranslationX:0.#},{PetImage.TranslationY:0.#} +{_petSize}] 缩放={_scaling} 外出={_outside} 活动区 {PetArea.Width:0.#}x{PetArea.Height:0.#}");
		if (_scaling)
			return;
		// 手势挂在活动区上,所以"点的是不是宠物"在这儿自己判:点在宠物矩形外的空白处不响应。
		// 拿不到坐标(平台不给)时按老行为认作点了宠物,别把功能整个挡掉。
		if (pos is { } p &&
			(p.X < PetImage.TranslationX || p.X > PetImage.TranslationX + _petSize ||
			 p.Y < PetImage.TranslationY || p.Y > PetImage.TranslationY + _petSize))
			return;
		var now = Environment.TickCount64;
		if (now - _lastTapMs > DoubleTapWindowMs)
			_tapCount = 0; // 隔太久,重新数
		_lastTapMs = now;
		if (++_tapCount >= 2)
		{
			_tapCount = 0;
			ShowPetMenu();
			return;
		}
		EnterMood(PetMood.Clicked, TimeSpan.FromSeconds(1));
	}

	// ---- 拖拽(手势挂在活动区上,见 PetPanel.xaml 里的说明;超 10px 才算拖,拖起切拖拽帧,松手从落点接着走) ----

	void OnPetPan(object? sender, PanUpdatedEventArgs e)
	{
		switch (e.StatusType)
		{
			case GestureStatus.Started:
				if (_scaling || _outside)
					return;
				_panStartX = PetImage.TranslationX;
				_panStartY = PetImage.TranslationY;
				_panWasWalking = _mood == PetMood.Walk;
				_panning = false;
				break;

			case GestureStatus.Running:
				if (!_panning && Math.Abs(e.TotalX) + Math.Abs(e.TotalY) > DragThresholdPx)
				{
					_panning = true; // 超阈值才算拖拽:手抖仍按点击处理(阈值与悬浮窗一致)
					_tapCount = 0;   // 拖拽不算连点,计数作废
					HidePetMenu();
					ExitMood();      // 拖起来先站住;动作/睡觉中拖 = 顺带唤醒
					PetImage.Source = ImgDrag;
				}
				if (_panning)
				{
					PetImage.TranslationX = Math.Clamp(_panStartX + e.TotalX, _walker.Min, _walker.Max);
					PetImage.TranslationY = Math.Clamp(_panStartY + e.TotalY, _walker.MinY, _walker.MaxY);
				}
				break;

			case GestureStatus.Completed:
			case GestureStatus.Canceled:
				if (!_panning)
					break;
				_panning = false;
				_targetY = PetImage.TranslationY;         // 垂直目标跟着落点,免得松手后自己飘回去
				_walker.Position = PetImage.TranslationX; // 走动引擎对齐新位置,从那接着走
				if (_panWasWalking && IsVisible && !_outside)
					EnterMood(PetMood.Walk);              // 拖之前在走:松手接着走
				else
					PetImage.Source = ImgNormal;
				break;
		}
	}

	// ---- 外出(桌面 PetFloatingWindow 的移动端形态:宠物走到系统悬浮窗上) ----

	/// <summary>外出/回家:服务在跑 = 收回;未授权 = 跳系统设置页,回来再点一次(悬浮窗权限无法运行时弹窗申请)。</summary>
	void OnGoOutClicked(object? sender, EventArgs e)
	{
		HidePetMenu();
		// 以服务真实状态为准(悬浮窗可能已被系统回收/从通知栏停掉),先校正面板标记
		if (_outside != PetOverlay.IsRunning)
			SetOutState(PetOverlay.IsRunning);
		if (_outside)
		{
			PetOverlay.Stop(); // 面板侧回家:停服务,画面本地同步(不等服务回调)
			SetOutState(false);
			return;
		}
		if (!PetOverlay.CanDrawOverlays())
		{
			PetOverlay.RequestOverlayPermission();
			_ = ExplainOverlayPermissionAsync();
			return;
		}
		// 落点 = 宠物此刻在小屋里的位置,换算成屏幕像素:活动区左上角(屏幕坐标) + 宠物 Translation(dip) × 密度。
		// 出门是原地接力,不该把它瞬移到屏幕角落;朝向也一并带出去,不然出去就左右颠倒(用户:"外出还会变成错误镜像")。
		var (sx, sy) = PetScreenPosition();
		var facingRight = PetImage.ScaleX < 0; // 小屋侧:朝右 = ScaleX -1(见 SetWalkFrame)
		var (areaX, areaY) = PetOverlay.GetScreenPosition(PetArea);
		Android.Util.Log.Info("QPET", $"外出:活动区屏幕 ({areaX},{areaY}) + 位移 ({PetImage.TranslationX:0.#},{PetImage.TranslationY:0.#})dp × 密度 {PetOverlay.Density:0.###} → 宠物屏幕 ({sx},{sy}) 朝右={facingRight}");
		if (PetOverlay.Start(sx, sy, facingRight))
			SetOutState(true);
	}

	/// <summary>宠物此刻在小屋里的屏幕坐标(左上角,像素)。小屋全用 dip 摆位(Translation),
	/// 悬浮窗全用像素 —— 统一在这里换算,两边对坐标只此一处。</summary>
	(int X, int Y) PetScreenPosition()
	{
		var (areaX, areaY) = PetOverlay.GetScreenPosition(PetArea);
		if (areaX == int.MinValue || areaY == int.MinValue)
			return (int.MinValue, int.MinValue);
		var density = PetOverlay.Density;
		return (areaX + (int)Math.Round(PetImage.TranslationX * density),
				areaY + (int)Math.Round(PetImage.TranslationY * density));
	}

	async Task ExplainOverlayPermissionAsync()
	{
		if (Shell.Current is { } shell)
			await shell.DisplayAlertAsync("还差一步", "请在设置页打开“显示在其他应用上层”,回到这里双击宠物、在菜单里再选一次“🚀 外出”。", "知道了");
	}

	/// <summary>悬浮窗菜单点了"回家":服务把宠物在屏幕上的坐标一并交回,宠物按它摆回小屋原地
	/// (服务先发事件再停止,此处只管画面)。不摆的话它会停在上一次的小屋位置,看着就是"回家位置一直在变"。</summary>
	void OnOverlayGoHome(int screenX, int screenY)
	{
		SetOutState(false);
		var (areaX, areaY) = PetOverlay.GetScreenPosition(PetArea);
		var density = PetOverlay.Density;
		if (screenX == int.MinValue || screenY == int.MinValue || areaX == int.MinValue || density <= 0)
		{
			Android.Util.Log.Warn("QPET", $"回家:坐标不可用(悬浮窗 {screenX},{screenY} / 活动区 {areaX},{areaY} / 密度 {density}),保持原位");
			return;
		}
		// 屏幕像素 → 活动区 dip:减去活动区左上角,再除以密度
		var x = (screenX - areaX) / density;
		var y = (screenY - areaY) / density;
		if (_walker.Max > _walker.Min)
			x = Math.Clamp(x, _walker.Min, _walker.Max);   // 悬浮窗能贴屏幕边,小屋有内边距:夹回可走范围
		if (_walker.MaxY > _walker.MinY)
			y = Math.Clamp(y, _walker.MinY, _walker.MaxY);
		PetImage.TranslationX = x;
		PetImage.TranslationY = y;
		_walker.Position = x; // 走动引擎对齐新落点:恢复走动时从这里接着走,不跳回旧位置
		_targetY = y;         // 垂直目标跟着落点,免得松手后自己飘回去
		Android.Util.Log.Info("QPET", $"回家:悬浮窗屏幕 ({screenX},{screenY}) → 小屋 ({x:0},{y:0})");
	}

	/// <summary>切换外出态:外出 = 面板宠物隐藏并停摆(让位给悬浮窗);回家 = 复位并接着走动/眨眼。</summary>
	void SetOutState(bool outside)
	{
		_outside = outside;
		PetImage.IsVisible = !outside;
		UpdateMenuButtons(); // 菜单里的"外出/回家"跟着切
		if (outside)
		{
			ExitMood();     // 动作/睡觉中外出:先回常态,回来时画面干净
			CancelTimers(); // ExitMood 可能刚起了眨眼,这里一并收掉
			StopWalkTimer();
			return;
		}
		if (!IsVisible)
			return;
		if (_mood == PetMood.Walk)
			StartWalkTimer();
		else if (_mood == PetMood.Normal)
			StartBlink();
	}

	// ---- 缩放(桌面 OnScale/OnScaleOk/OnSizeSliderChanged 同构;80-400 px) ----

	void OnScaleClicked(object? sender, EventArgs e)
	{
		HidePetMenu();
		if (_scaling)
		{
			CancelScale(); // 再点一次 = 取消,还原尺寸
			return;
		}
		_scaling = true;
		_petSizeBase = _petSize;
		ExitMood(); // 回常态站住(动作/睡觉中进缩放先恢复)
		ApplyPetSize();
		ScaleUi.IsVisible = true;
		UpdateMenuButtons();
	}

	/// <summary>取消缩放:还原进入缩放时的尺寸。</summary>
	void CancelScale()
	{
		_petSize = _petSizeBase;
		ApplyPetSize();
		ExitScaleMode();
	}

	/// <summary>确定缩放:存配置并持久化,上传云端(服务器推回确认,同值无感跳过),更新走动范围。</summary>
	void OnScaleOkClicked(object? sender, EventArgs e)
	{
		PetConfig.PetSize = _petSize;
		PetConfig.Save(AppState.Settings); // 本地持久化,下次打开生效(桌面 SaveSettings 同)
		if (AppState.Client.IsConnected)
			AppState.Client.UpdatePetSize(_petSize); // 云端:同账号多端实时同步
		UpdateWalkRange();
		ClampPetPosition(); // 尺寸变小防越界
		ExitScaleMode();
	}

	void OnScaleCancelClicked(object? sender, EventArgs e) => CancelScale();

	void ExitScaleMode()
	{
		_scaling = false;
		ScaleUi.IsVisible = false;
		UpdateMenuButtons();
	}

	/// <summary>滑块实时应用尺寸(程序回写 / 非缩放模式跳过,桌面同)。</summary>
	void OnSizeSliderChanged(object? sender, ValueChangedEventArgs e)
	{
		if (!_scaling || _syncingSlider)
			return;
		_petSize = e.NewValue;
		ApplyPetSize();
	}

	/// <summary>应用当前尺寸到宠物画面与尺寸文字(缩放中同步滑块,防回环)。</summary>
	void ApplyPetSize()
	{
		PetImage.WidthRequest = _petSize;
		PetImage.HeightRequest = _petSize;
		SizeText.Text = $"{_petSize:F0} px";
		if (_scaling && Math.Abs(SizeSlider.Value - _petSize) > 0.5)
		{
			_syncingSlider = true;
			SizeSlider.Value = _petSize;
			_syncingSlider = false;
		}
	}

	// ---- 云端尺寸(桌面 App.OnCloudPetSize → PetHouseView.ApplyCloudSize 同构) ----

	/// <summary>云端尺寸到达:配置已由 AppState 持久化。未在手动缩放即应用;页面不可见只记数值
	/// (回来 RefreshLayout 按 PetConfig 摆);确认后服务器回推同值,差值比较直接跳过。</summary>
	void OnCloudPetSize(double size)
	{
		_petSize = size;
		if (!IsVisible || _scaling)
			return;
		if (Math.Abs(PetConfig.PetSize - _petSize) < 0.01)
			return;
		ApplyPetSize();
		UpdateWalkRange();
		ClampPetPosition();
	}

	// ---- 工具 ----

	/// <summary>取消并换新计时器(隐藏 / 状态切换 / 眨眼停)。</summary>
	void CancelTimers()
	{
		_cts.Cancel();
		_cts.Dispose();
		_cts = new CancellationTokenSource();
		_blinkTask = null;
	}
}
