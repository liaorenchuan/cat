using Microsoft.Extensions.DependencyInjection;

namespace QPet.Mobile;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		// 锁定浅色:页面视觉全为硬编码浅色(白卡片/奶油渐变),控件隐式样式却是
		// AppThemeBinding 跟随系统;不锁的话系统深色下输入框会白底白字(见 Styles.xaml 的 Entry/Editor 样式)
		UserAppTheme = AppTheme.Light;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}