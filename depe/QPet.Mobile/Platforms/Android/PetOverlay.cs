using Android.Content;
using Microsoft.Maui.ApplicationModel;

namespace QPet.Mobile;

/// <summary>
/// 宠物悬浮窗入口(Android):权限检查、引导授权、启停前台服务。
/// 悬浮窗权限是特殊授权(API 23+ 无法运行时弹窗,只能跳系统设置页让用户手动开"显示在其他应用上层");
/// Android 13+ 另需通知权限,否则前台服务的常驻通知不显示(服务本身仍能跑)。
/// </summary>
public static class PetOverlay
{
	/// <summary>悬浮窗是否已获授权。API 21-22 无此开关,SYSTEM_ALERT_WINDOW 装机即授予。</summary>
	public static bool CanDrawOverlays()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(23))
			return true;
		return Android.Provider.Settings.CanDrawOverlays(Platform.AppContext);
	}

	/// <summary>跳系统设置页引导用户开"显示在其他应用上层"。返回当前是否已授权(首次调用通常为 false,授权后需再次点击)。</summary>
	public static bool RequestOverlayPermission()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(23))
			return true; // API 21-22:无此开关,装机即授予
		if (CanDrawOverlays())
			return true;
		var activity = Platform.CurrentActivity;
		if (activity is null)
			return false;
		try
		{
			// 带 package: 的 ActionManageOverlayPermission 会直达本应用的授权开关页
			var intent = new Intent(Android.Provider.Settings.ActionManageOverlayPermission);
			intent.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
			activity.StartActivity(intent);
		}
		catch
		{
			// 个别 ROM 无此设置页:交给调用方提示用户手动去设置里开
		}
		return false;
	}

	/// <summary>请求 Android 13+ 通知权限(前台服务常驻通知用)。返回是否已授予。</summary>
	public static bool EnsureNotificationPermission()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(33))
			return true;
		var activity = Platform.CurrentActivity;
		if (activity is null)
			return false;
		if (activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS")
			== Android.Content.PM.Permission.Granted)
			return true;
		activity.RequestPermissions(new[] { "android.permission.POST_NOTIFICATIONS" }, 0x5150);
		return false;
	}

	/// <summary>把宠物放出去:未授权则跳授权页并返回 false(下次点击再放)。
	/// startX/startY = 宠物此刻在屏幕上的左上角(小屋里的位置),悬浮窗按它落座 —— 出门前在哪,出门后还在哪。
	/// facingRight = 宠物此刻在小屋里的朝向,不接过来出门就左右颠倒。</summary>
	public static bool Start(int startX = int.MinValue, int startY = int.MinValue, bool facingRight = false)
	{
		if (!CanDrawOverlays())
		{
			RequestOverlayPermission();
			return false; // 用户去设置页授权,回来再点一次
		}
		EnsureNotificationPermission();
		PetOverlayService.PendingStartX = startX; // 同进程静态传递:服务 OnCreate 里取用(Intent 会被同名类型挡住,不折腾)
		PetOverlayService.PendingStartY = startY;
		PetOverlayService.PendingStartFacingRight = facingRight;
		Android.Util.Log.Info("QPET", $"外出:小屋落点屏幕坐标 ({startX},{startY}) 朝右={facingRight}");
		var ctx = Platform.AppContext;
		var intent = new Intent(ctx, typeof(PetOverlayService));
		if (OperatingSystem.IsAndroidVersionAtLeast(26))
			ctx.StartForegroundService(intent); // 8.0+ 后台限制:必须由前台服务启动
		else
			ctx.StartService(intent);           // 旧机型无 StartForegroundService:普通启动,服务内 StartForeground 照常生效
		return true;
	}

	/// <summary>取元素在屏幕上的左上角(像素)。外出时把宠物在小屋里的位置交给悬浮窗,
	/// 免得"出门"这一步把它瞬移到屏幕右下角(桌面端是同屏接力,位置本来就是连着的)。</summary>
	public static (int X, int Y) GetScreenPosition(Microsoft.Maui.Controls.VisualElement element)
	{
		if (element.Handler?.PlatformView is Android.Views.View native)
		{
			var loc = new int[2];
			native.GetLocationOnScreen(loc);
			return (loc[0], loc[1]);
		}
		return (int.MinValue, int.MinValue);
	}

	/// <summary>屏幕密度(Android 上 像素 = dip × density)。小屋侧全用 dip 摆位、悬浮窗全用像素,
	/// 两边对坐标就靠这一把尺子 —— 同一只宠物屋里屋外一样大也是它换算的(见 PetOverlayView.PetSizeFromDp)。</summary>
	public static double Density => Platform.AppContext.Resources?.DisplayMetrics?.Density ?? 1.0;

	/// <summary>收起悬浮窗(宠物的"回家"动作:停服务,面板侧监听 GoHome 恢复小屋形态)。</summary>
	public static void Stop()
	{
		var ctx = Platform.AppContext;
		ctx.StopService(new Intent(ctx, typeof(PetOverlayService)));
	}

	/// <summary>悬浮窗当前是否在显示。</summary>
	public static bool IsRunning => PetOverlayService.IsRunning;
}
