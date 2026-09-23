namespace QPet.Mobile.Utils;

/// <summary>
/// 页面"只是被覆盖"的统一判定 —— 各页订阅保持/退订的那条分界线(C4)。
/// 被 push 覆盖(本页之上还有页面)= 保持订阅,等回来继续用;被 pop 弹回列表 = 退订。
///
/// 不要用 <c>NavigationStack.Contains(this)</c>:pop 动画期间本页可能还留在栈里,
/// 会被误判成"被覆盖"而残留订阅 —— 残留的聊天页会把退出后到达的新消息继续标成已读,
/// 消息列表红点就再也不亮了。这里额外要求"不在栈顶"才算被覆盖。
/// </summary>
public static class NavGuard
{
	/// <summary>本页在导航栈里,且上面还压着别的页面(= 只是被覆盖,随时会回来)。</summary>
	public static bool IsCovered(Page page)
	{
		var stack = Shell.Current?.Navigation?.NavigationStack;
		if (stack is null || stack.Count < 2)
			return false; // 栈里只有自己(或拿不到栈):当作真的离开,退订更安全
		for (var i = 0; i < stack.Count; i++)
		{
			if (ReferenceEquals(stack[i], page))
				return i != stack.Count - 1; // 在栈里但不是栈顶 = 被覆盖
		}
		return false; // 已经不在栈里 = 真离开了
	}
}
