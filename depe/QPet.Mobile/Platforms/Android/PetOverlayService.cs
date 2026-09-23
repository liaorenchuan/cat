using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;

namespace QPet.Mobile;

/// <summary>
/// 宠物悬浮窗前台服务:常驻通知保活 + 用 WindowManager 把宠物挂到系统层(显示在其他应用上层)。
/// 走动/拖拽/双击呼出菜单都在 PetOverlayView 里,本服务只管窗口生命周期与通知。
/// </summary>
[Service(Name = "com.qpet.mobile.PetOverlayService", Exported = false)]
public class PetOverlayService : Service
{
	private const string ChannelId = "qpet_pet_overlay";
	private const int NotificationId = 0x5151;

	/// <summary>宠物此刻在屏幕上的左上角(小屋里的位置),由 PetOverlay.Start 在同进程内先放好,
	/// 服务 OnCreate 里取用 —— 出门落点要"原地接力",不能瞬移到屏幕角落。</summary>
	public static int PendingStartX { get; set; } = int.MinValue;
	public static int PendingStartY { get; set; } = int.MinValue;

	/// <summary>出门前宠物在小屋里的朝向(true = 朝右),同样同进程静态传递:不接过来出门就镜像反了。</summary>
	public static bool PendingStartFacingRight { get; set; }

	/// <summary>服务是否在跑(= 悬浮窗是否显示)。</summary>
	public static bool IsRunning { get; private set; }

	/// <summary>悬浮窗菜单点了"回家":带上宠物此刻在屏幕上的左上角(像素),
	/// MAUI 侧按这个坐标把小屋里的宠物摆回去 —— 回家也是原地接力,位置不该跳。</summary>
	public static event Action<int, int>? GoHomeRequested;

	private IWindowManager? _windowManager;
	private PetOverlayView? _view;

	public override void OnCreate()
	{
		base.OnCreate();
		IsRunning = true;
		CreateNotificationChannel();
		// Android 14+ 强制要求 startForeground 时指明类型:manifest 里声明了 foregroundServiceType 还不够,
		// 调用不传类型系统就判 "does not have any types" 并把服务立即停掉 —— 悬浮窗刚挂上去就被撤掉,
		// 面板却因为 Start() 无条件返回 true 显示成"已外出"。日志佐证:
		//   FGS stop call for: 10209 has no types!(服务启动 36ms 后即被停)
		// API 34 以下没有 specialUse 类型,继续走两参数重载。
		try
		{
			if (OperatingSystem.IsAndroidVersionAtLeast(34))
				StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeSpecialUse);
			else
				StartForeground(NotificationId, BuildNotification());
		}
		catch (Exception ex)
		{
			Android.Util.Log.Error("QPET", $"StartForeground 失败: {ex}");
			throw;
		}

		_windowManager = ResolveWindowManager();
		if (_windowManager is null)
		{
			StopSelf(); // 拿不到窗口服务:直接收摊,不留半个空服务
			return;
		}
		// 落点:面板把宠物当前的小屋坐标先放好(没给就按屏幕右下角兜底)
		_view = new PetOverlayView(this, OnGoHome, _windowManager, PendingStartX, PendingStartY, PendingStartFacingRight); // 把手由本服务解析并校验非空后才传:View 内部自己取恒为 null
		PendingStartX = PendingStartY = int.MinValue; // 用掉即清:下次没传就是兜底位置
		PendingStartFacingRight = false;
		_windowManager.AddView(_view, _view.CreateLayoutParams());
	}

	/// <summary>用户主动放出去/收回的,被杀后不自动重启(重启会凭空冒出一只宠物)。</summary>
	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
		=> StartCommandResult.NotSticky;

	public override IBinder? OnBind(Intent? intent) => null;

	public override void OnDestroy()
	{
		IsRunning = false;
		if (_view is not null)
		{
			_view.Stop();
			try { _windowManager?.RemoveView(_view); } catch { }
			_view.Dispose();
			_view = null;
		}
		if (OperatingSystem.IsAndroidVersionAtLeast(24))
			StopForeground(StopForegroundFlags.Remove);
		else
#pragma warning disable CA1422 // 旧机型兼容:API 24 以下只有 bool 重载
			StopForeground(true);
#pragma warning restore CA1422
		base.OnDestroy();
	}

	/// <summary>
	/// 取 WindowManager。踩过的坑:Context.GetSystemService(window) 返回的是 Java.Lang.Object
	/// 裸包装,托管侧没有解析成具体类型,所以 "as IWindowManager" 恒为 null —— 服务随即 StopSelf,
	/// 悬浮窗一闪即逝(日志:GetSystemService(window)=Java.Lang.Object, as IWindowManager=失败)。
	/// 两条路:Activity 上的 WindowManager 是强类型绑定属性;Context 那条必须走 JavaCast,
	/// 由 JNI 侧按真实类型解析出接口。
	/// </summary>
	private IWindowManager? ResolveWindowManager()
	{
		try
		{
			var wm = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.WindowManager;
			if (wm is not null)
				return wm;
			Android.Util.Log.Warn("QPET", "CurrentActivity.WindowManager 为空,改用 GetSystemService + JavaCast");
		}
		catch (Exception ex)
		{
			Android.Util.Log.Warn("QPET", $"CurrentActivity 取窗口服务异常: {ex.GetType().Name} - {ex.Message}");
		}

		foreach (var (name, ctx) in new (string, Context)[] { ("Service", this), ("Application", Android.App.Application.Context) })
		{
			try
			{
				var svc = ctx.GetSystemService(WindowService);
				var wm = svc?.JavaCast<IWindowManager>();
				Android.Util.Log.Info("QPET", $"{name}: JavaCast<IWindowManager> = {(wm is null ? "失败" : "成功")}");
				if (wm is not null)
					return wm;
			}
			catch (Exception ex)
			{
				Android.Util.Log.Warn("QPET", $"{name}: 取窗口服务异常 {ex.GetType().Name} - {ex.Message}");
			}
		}
		return null;
	}

	/// <summary>悬浮窗菜单"回家":把宠物此刻的屏幕坐标一并交回 MAUI 侧(面板原地摆回),然后收起悬浮窗。</summary>
	private void OnGoHome()
	{
		var (x, y) = _view?.ScreenPosition() ?? (int.MinValue, int.MinValue);
		Android.Util.Log.Info("QPET", $"回家:悬浮窗屏幕坐标 ({x},{y})");
		GoHomeRequested?.Invoke(x, y);
		StopSelf();
	}

	private void CreateNotificationChannel()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26))
			return;
		if (GetSystemService(NotificationService) is not NotificationManager mgr)
			return;
		var channel = new NotificationChannel(ChannelId, "宠物悬浮窗", NotificationImportance.Low)
		{
			Description = "让宠物显示在其他应用上层",
		};
		mgr.CreateNotificationChannel(channel);
	}

	private Notification BuildNotification()
	{
		var intent = new Intent(this, typeof(MainActivity));
		intent.SetFlags(ActivityFlags.SingleTop);
		var flags = PendingIntentFlags.UpdateCurrent;
		if (OperatingSystem.IsAndroidVersionAtLeast(23))
			flags |= PendingIntentFlags.Immutable; // Android 12+ 强制要求指明可变性
		var pending = PendingIntent.GetActivity(this, 0, intent, flags);

		var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
			? new Notification.Builder(this, ChannelId)
			: new Notification.Builder(this);
		return builder
			.SetContentTitle("Q版桌宠")
			.SetContentText("宠物正在屏幕上玩耍;双击宠物打开菜单")
			.SetSmallIcon(NotificationIcon())
			.SetContentIntent(pending)
			.SetOngoing(true)
			.Build();
	}

	/// <summary>通知小图标:优先用应用图标,取不到就退回系统信息图标(避免图标名不匹配直接崩)。</summary>
	private int NotificationIcon()
	{
		var id = Resources?.GetIdentifier("appicon", "mipmap", PackageName) ?? 0;
		return id != 0 ? id : Android.Resource.Drawable.IcDialogInfo;
	}
}
