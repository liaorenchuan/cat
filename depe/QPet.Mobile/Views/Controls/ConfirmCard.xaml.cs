namespace QPet.Mobile.Views.Controls;

/// <summary>
/// 确认卡:标题 + 说明 + 取消/确认(IsDanger=true 时确认按钮转红)。
/// 由页面 overlay 承载居中展示;事件在页面 code-behind 接线。
/// </summary>
public partial class ConfirmCard : ContentView
{
	public static readonly BindableProperty IsDangerProperty =
		BindableProperty.Create(nameof(IsDanger), typeof(bool), typeof(ConfirmCard), false);

	public ConfirmCard()
	{
		InitializeComponent();
	}

	public bool IsDanger
	{
		get => (bool)GetValue(IsDangerProperty);
		set => SetValue(IsDangerProperty, value);
	}

	/// <summary>标题文案(如"删除好友")。页面弹窗前设置。</summary>
	public string TitleText
	{
		set => TitleLabel.Text = value;
	}

	/// <summary>说明文案(多行自动换行)。</summary>
	public string MessageText
	{
		set => MessageLabel.Text = value;
	}

	/// <summary>确认按钮文案(默认"确认",删除/退出时改为 删除/退出)。</summary>
	public string ConfirmText
	{
		set => ConfirmButton.Text = value;
	}

	public event EventHandler? CancelRequested;
	public event EventHandler? ConfirmRequested;

	void OnCancelClicked(object? sender, EventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
	void OnConfirmClicked(object? sender, EventArgs e) => ConfirmRequested?.Invoke(this, EventArgs.Empty);
}
