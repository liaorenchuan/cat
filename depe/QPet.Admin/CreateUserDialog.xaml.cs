using System.Windows;
using System.Windows.Input;

namespace QPet.Admin;

/// <summary>创建账户弹窗(账号/密码/昵称)。确定返回 true,三值取 Account/Password/Nickname。</summary>
public partial class CreateUserDialog : Window
{
    public CreateUserDialog()
    {
        InitializeComponent();
        NickBox.Focus(); // 昵称在第一个字段,默认选中昵称
    }

    public string Account { get; private set; } = "";
    public string Password { get; private set; } = "";
    public string Nickname { get; private set; } = "";

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var account = AccountBox.Text.Trim();
        var password = PassBox.Password;
        var nickname = NickBox.Text.Trim();
        if (account.Length < 2 || account.Length > 20)
        {
            ShowErr("账号需为 2-20 位");
            return;
        }
        if (password.Length < 4)
        {
            ShowErr("密码至少 4 位");
            return;
        }
        if (nickname.Length == 0 || nickname.Length > 20)
        {
            ShowErr("昵称需为 1-20 字");
            return;
        }
        Account = account;
        Password = password;
        Nickname = nickname;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnOk(sender, e);
    }

    private void ShowErr(string msg)
    {
        ErrText.Text = msg;
        ErrText.Visibility = Visibility.Visible;
    }
}
