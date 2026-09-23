namespace QPet.Mobile.Views.Controls;

/// <summary>
/// 输入卡:改备注/改群名/改昵称共用。页面弹窗前设 TitleText/HintText,
/// 确定时页面自行校验,出错调 ShowError;卡片自身不含业务规则。
/// </summary>
public partial class PromptCard : ContentView
{
	public PromptCard()
	{
		InitializeComponent();
	}

	public string TitleText
	{
		set => TitleLabel.Text = value;
	}

	/// <summary>输入框提示语(占位,如"给好友起个新名字")。</summary>
	public string PromptPlaceholder
	{
		set => ValueEditor.Placeholder = value;
	}

	/// <summary>小字提示行(展示在输入框上方,如限制说明);为空自动隐藏。</summary>
	public string HintText
	{
		set
		{
			HintLabel.Text = value;
			HintLabel.IsVisible = value.Length > 0;
		}
	}

	/// <summary>弹窗预填值(如当前备注/昵称)。</summary>
	public string InitialValue
	{
		set => ValueEditor.Text = value;
	}

	/// <summary>当前输入(trim 过)。</summary>
	public string Value => ValueEditor.Text?.Trim() ?? "";

	/// <summary>展示校验红字(输入非法);重新输入时由页面清掉。</summary>
	public void ShowError(string message)
	{
		StatusLabel.Text = message;
		StatusLabel.IsVisible = true;
	}

	/// <summary>清除红字并自动收起,弹窗打开时可调用。</summary>
	public void ClearError() => StatusLabel.IsVisible = false;

	/// <summary>弹窗出现后把焦点给输入框并弹出软键盘。</summary>
	public void FocusInput()
	{
		ClearError();
		ValueEditor.Focus();
	}

	public event EventHandler? CancelRequested;
	public event EventHandler? ConfirmRequested;

	void OnCancelClicked(object? sender, EventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
	void OnConfirmClicked(object? sender, EventArgs e) => ConfirmRequested?.Invoke(this, EventArgs.Empty);
}
