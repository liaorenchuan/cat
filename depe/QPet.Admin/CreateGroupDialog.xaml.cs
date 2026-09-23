using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QPet.Admin.Services;
using QPet.Ui;

namespace QPet.Admin;

/// <summary>
/// 选成员建群(管理端版):群名 + 全用户多选(与客户端选好友建群同款交互,
/// 但管理端可拉任意用户,无需好友关系)。群主固定为详情页当前账号,勾选结果转账号串提交。
/// </summary>
public partial class CreateGroupDialog : AdminDialogBase
{
    private readonly string _ownerAccount;
    private readonly List<CheckBox> _boxes = new();

    public CreateGroupDialog(AdminApiClient api, string ownerAccount) : base(api)
    {
        InitializeComponent();
        _ownerAccount = ownerAccount;
        _ = LoadUsersAsync();
    }

    protected override TextBlock MsgBox => ErrorText;

    /// <summary>加载全部用户生成勾选框(群主自己固定入群,不出现在列表)。</summary>
    private async Task LoadUsersAsync()
    {
        try
        {
            using var r = await Api.GetAsync("users");
            if (!r.IsSuccess)
            {
                ShowErr(r.Unauthorized
                    ? "登录已过期,请重新登录管理端"
                    : $"加载用户失败 ({r.Status})");
                return;
            }

            using var doc = r.Doc!;
            foreach (var item in doc.RootElement.GetProperty("users").EnumerateArray())
            {
                var account = item.GetProperty("account").GetString() ?? "";
                if (account == _ownerAccount)
                    continue; // 群主固定入群
                var nickname = item.GetProperty("nickname").GetString() ?? "";
                var box = MakeUserCheckBox(account, nickname, account);
                _boxes.Add(box);
                MemberPanel.Children.Add(box);
            }
            if (_boxes.Count == 0)
            {
                MemberPanel.Children.Add(new TextBlock
                {
                    Text = "还没有其他用户",
                    FontSize = 12,
                    Foreground = UiPalette.Secondary,
                });
            }
        }
        catch (Exception ex)
        {
            ShowErr("加载失败: " + ex.Message);
        }
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = CreateAsync();
    }

    private void OnCreate(object sender, RoutedEventArgs e) => _ = CreateAsync();

    private async Task CreateAsync()
    {
        var name = NameBox.Text.Trim();
        var selected = _boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        ErrorText.Visibility = Visibility.Collapsed;
        if (name.Length == 0)
        {
            ShowErr("请输入群名称");
            return;
        }
        if (name.Length > 20)
        {
            ShowErr("群名需为 1-20 字");
            return;
        }

        if (!await PostAsync("createGroup",
                new { ownerAccount = _ownerAccount, name, memberAccounts = string.Join(",", selected) }))
            return;
        DialogResult = true; // 成功后由详情页刷新并提示
    }
}
