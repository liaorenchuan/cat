using System.Windows;
using QPet.Core;
using QPet.Ui.Dialogs;

namespace QPet.Wpf.Dialogs;

/// <summary>
/// 群聊详情页:群名(可改,全员可见) + 群号 + 成员数 + 群备注(本地,只有自己可见) +
/// 💬 发消息(开会话) + 🚪 退出群聊(群主退出 = 解散)。公共群聊隐藏改名/退出。
/// </summary>
public partial class GroupDetailDialog : Window
{
    private readonly SyncClient _client;
    private readonly GroupInfo _group;
    private readonly Action _onOpenChat;

    public GroupDetailDialog(SyncClient client, GroupInfo group, Action onOpenChat)
    {
        InitializeComponent();
        _client = client;
        _group = group;
        _onOpenChat = onOpenChat;

        // 头部:群号直接读服务器(公共群固定 000000,普通群递增);
        // 创建者显示群主昵称(不暴露内部 userId)
        NameText.Text = group.Name;
        NumText.Text = group.GroupId == "public"
            ? $"公共群聊 · 群号: {group.Num}"
            : group.Num.Length > 0 ? $"群号: {group.Num}" : "";
        var ownerNick = group.Members.FirstOrDefault(m => m.UserId == group.OwnerId)?.Nickname ?? "";
        MemberText.Text = group.GroupId == "public"
            ? $"成员 {group.Members.Count} 人"
            : $"成员 {group.Members.Count} 人 · 创建者 {ownerNick}";

        // 公共群聊:默认群,人人都在,不可改名/退出/备注
        var isPublic = group.GroupId == "public";
        RenamePanel.Visibility = isPublic ? Visibility.Collapsed : Visibility.Visible;
        LeaveButton.Visibility = isPublic ? Visibility.Collapsed : Visibility.Visible;

        // 群名输入框初值 + 云端群备注回填(换设备登录也有)
        NameBox.Text = group.Name;
        RemarkBox.Text = client.LastSnapshot.Settings.GetValueOrDefault("GroupRemark." + group.GroupId) ?? "";
    }

    private void OnSaveName(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            // 输入为空:不改名,输入框与群名都恢复原来的名字
            NameBox.Text = _group.Name;
            ErrorText.Text = "群名不能为空,已恢复原群名";
            return;
        }
        if (name.Length > 20)
        {
            ErrorText.Text = "群名最多 20 字";
            return;
        }
        _client.RenameGroup(_group.GroupId, name);
        NameText.Text = name;
        ErrorText.Text = "已提交,群成员刷新后可见新群名 ✓";
    }

    private void OnSaveRemark(object sender, RoutedEventArgs e)
    {
        var remark = RemarkBox.Text.Trim();
        if (remark.Length > 20)
        {
            ErrorText.Text = "备注最多 20 字";
            return;
        }
        // 云端保存:服务器落库并回推本账号全部连接(消息页/其他设备同步生效)
        _client.SetSetting("GroupRemark." + _group.GroupId, remark); // 空串 = 取消备注
        _client.LastSnapshot.Settings["GroupRemark." + _group.GroupId] = remark;
        ErrorText.Text = "备注已保存 ✓";
    }

    private void OnOpenChat(object sender, RoutedEventArgs e)
    {
        _onOpenChat();
        Close();
    }

    private void OnLeaveGroup(object sender, RoutedEventArgs e)
    {
        if (new ConfirmDialog("退出群聊",
                $"确定退出群聊「{_group.Name}」吗?\n(群主退出将解散该群,群聊记录一并删除)",
                okText: "是", cancelText: "否") { Owner = this }.ShowDialog() != true)
            return;
        _client.LeaveGroup(_group.GroupId);
        Close(); // 列表由 ChatView 的 groupRemoved 事件刷新
    }
}
