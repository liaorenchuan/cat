using System.Windows;
using System.Windows.Input;

namespace QPet.Admin;

/// <summary>改群名弹窗:预填当前群名并全选,确定返回新名(1-20 字校验)。</summary>
public partial class RenameGroupDialog : Window
{
    public RenameGroupDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    public string NewName { get; private set; } = "";

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || name.Length > 20)
        {
            ErrText.Text = "群名需为 1-20 字";
            ErrText.Visibility = Visibility.Visible;
            return;
        }
        NewName = name;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnOk(sender, e);
    }
}
