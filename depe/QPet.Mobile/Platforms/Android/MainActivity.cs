using Android.App;
using Android.Content.PM;
using Android.OS;
using QPet.Mobile.Services;

namespace QPet.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	/// <summary>退到后台就落盘离线缓存(D1):否则缓存永远停在"登录那一刻",
	/// 本次会话收到的消息/未读/备注一杀进程全丢。同步写(几十毫秒),保证返回前真的写完。</summary>
	protected override void OnStop()
	{
		AppState.SaveCacheNow();
		base.OnStop();
	}
}
