using System.Windows;
using System.Windows.Controls;
using QPet.Admin.Services;
using QPet.Ui;

namespace QPet.Admin;

/// <summary>
/// 群聊管理-添加成员:全用户勾选(排除已在群的),逐个直接加群(跳过申请审批)。
/// 全部成功才关闭;部分失败留在对话框,已成功的从列表移除可重试剩余。
/// </summary>
public partial class AddGroupMembersDialog : AdminDialogBase
{
    private readonly string _groupNum;
    private readonly List<CheckBox> _boxes = new();
    private readonly Dictionary<string, string> _accounts = new(); // userId → account(提示用)

    public AddGroupMembersDialog(AdminApiClient api,
        string groupNum, string groupName, System.Collections.Generic.IEnumerable<string> memberIds) : base(api)
    {
        InitializeComponent();
        _groupNum = groupNum;
        TitleText.Text = $"向「{groupName}」(群号 {groupNum})添加成员:勾选后直接加入,无需好友关系。";
        var exclude = new HashSet<string>(memberIds);
        _ = LoadUsersAsync(exclude);
    }

    /// <summary>加载全部用户生成勾选框(已在群的自动排除)。</summary>
    private async Task LoadUsersAsync(HashSet<string> exclude)
    {
        try
        {
            using var r = await Api.GetAsync("users");
            if (!r.IsSuccess)
            {
                ShowErr(r.Unauthorized
                    ? "登录已过期,请重新登录管理端"
                    : $"加载用户失败 ({r.Status})");
                AddButton.IsEnabled = false;
                return;
            }

            using var doc = r.Doc!;
            foreach (var item in doc.RootElement.GetProperty("users").EnumerateArray())
            {
                var userId = item.GetProperty("userId").GetString() ?? "";
                if (exclude.Contains(userId))
                    continue; // 已是该群成员
                var account = item.GetProperty("account").GetString() ?? "";
                var nickname = item.GetProperty("nickname").GetString() ?? "";
                _accounts[userId] = account;
                var box = MakeUserCheckBox(account, nickname, userId);
                _boxes.Add(box);
                MemberPanel.Children.Add(box);
            }
            if (_boxes.Count == 0)
            {
                MemberPanel.Children.Add(new TextBlock
                {
                    Text = "所有用户都已在该群中",
                    FontSize = 12,
                    Foreground = UiPalette.Secondary,
                });
                AddButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            ShowErr("加载失败: " + ex.Message);
            AddButton.IsEnabled = false;
        }
    }

    protected override TextBlock MsgBox => ErrorText;

    private void OnAdd(object sender, RoutedEventArgs e) => _ = AddAsync();

    /// <summary>逐个直接加群;成功的从列表移除,全部成功才关闭。</summary>
    private async Task AddAsync()
    {
        var selected = _boxes.Where(b => b.IsChecked == true).ToList();
        ErrorText.Visibility = Visibility.Collapsed;
        if (selected.Count == 0)
        {
            ShowErr("请先勾选要添加的成员");
            return;
        }
        var okCount = 0;
        string? firstFail = null;
        foreach (var box in selected)
        {
            var userId = (string)box.Tag;
            try
            {
                using var r = await Api.PostAsync("addToGroup", new { userId, groupNum = _groupNum });
                if (r.Unauthorized)
                {
                    ShowErr("登录已过期,请重新登录管理端");
                    return;
                }
                if (!r.IsSuccess)
                {
                    firstFail ??= $"{_accounts.GetValueOrDefault(userId)}: {r.Error}";
                    continue;
                }
            }
            catch (Exception ex)
            {
                firstFail ??= $"{_accounts.GetValueOrDefault(userId)}: {ex.Message}";
                continue;
            }
            okCount++;
            // 已成功:从列表移除(视觉消失,重试时不再重复)
            _boxes.Remove(box);
            MemberPanel.Children.Remove(box);
        }
        if (okCount == selected.Count)
        {
            DialogResult = true; // 全部成功,关窗由群管理页刷新
            return;
        }
        ShowErr($"成功 {okCount} / {selected.Count} 个,失败: {firstFail}");
    }
}
