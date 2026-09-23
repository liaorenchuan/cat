using System.Windows;
using System.Windows.Input;

namespace QPet.Wpf.Dialogs;

/// <summary>通用单行输入弹窗(备注 / 改名 / 改昵称)。ShowDialog 返回后读 Value。</summary>
public partial class PromptDialog : Window
{
    public PromptDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
    }

    /// <summary>用户输入的内容(确定后有效)。</summary>
    public string Value
    {
        get => ValueBox.Text;
        set => ValueBox.Text = value;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnOk(sender, e);
        else if (e.Key == Key.Escape)
            OnCancel(sender, e);
    }
}
