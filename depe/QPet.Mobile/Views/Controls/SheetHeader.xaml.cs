namespace QPet.Mobile.Views.Controls;

/// <summary>
/// 页签头:push 出来的次级页面顶部共用条(页面壳隐藏 NavBar,靠它返回)。
/// 标题在 XAML 上用 SheetTitle 子元素直接绑;关闭事件页面 pop 自己。
/// </summary>
public partial class SheetHeader : ContentView
{
	public SheetHeader()
	{
		InitializeComponent();
	}

	public string Title
	{
		set => TitleLabel.Text = value;
	}

	public event EventHandler? CloseRequested;

	void OnCloseTapped(object? sender, TappedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
