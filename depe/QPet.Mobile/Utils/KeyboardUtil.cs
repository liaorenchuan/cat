using Microsoft.Maui.ApplicationModel;
#if ANDROID
using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
#endif

namespace QPet.Mobile.Utils;

/// <summary>
/// 收起软键盘的跨页共用实现(聊天页/搜索页等输入框页面都调这个)。
/// Android 走 InputMethodManager;桌面端没有软键盘,空操作。
/// </summary>
public static class KeyboardUtil
{
	public static void HideKeyboard()
	{
#if ANDROID
		var activity = Platform.CurrentActivity;
		if (activity is null)
			return;
		// 焦点 view 拿不到 window token 时,兜底用 DecorView 根视图的 token
		var view = activity.CurrentFocus;
		var token = view?.WindowToken ?? activity.Window?.DecorView?.RootView?.WindowToken;
		if (token is not null)
		{
			var imm = (InputMethodManager)activity.GetSystemService(Context.InputMethodService)!;
			imm.HideSoftInputFromWindow(token, 0);
		}
		view?.ClearFocus();
#endif
	}
}
