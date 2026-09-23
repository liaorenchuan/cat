using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QPet.Core;
using QPet.Ui;

namespace QPet.Wpf.Dialogs;

/// <summary>
/// 搜索加群:输入群名或群号 → 服务器模糊搜(全库,公共群排最前)→
/// 候选列表每行:群名/类型/人数 + 加入按钮。"加入群聊"(公共群可直接加)或"申请加入"(好友群)。
/// 加入成功由 groupUpdated 推送,ChatView 列表自动刷新。
/// </summary>
public partial class SearchGroupDialog : Window
{
    private readonly SyncClient _client;

    /// <summary>候选行包装(群名/类型徽章/人数/按钮状态)。</summary>
    private sealed class GroupItem
    {
        public GroupSearchResult R = null!;
        // 头像:👥 绿块,与群聊条目一致;类型由徽章表达
        public string AvatarChar => "👥";
        public Brush AvatarBrush => UiPalette.GreenBg;
        public string Name => R.Name;
        public string KindText => R.IsPublic ? "公共群" : "好友群";
        public string MetaText => $"群号 {R.Num} · {R.MemberCount} 人";
        public string BtnText => R.IsOwner ? "你创建的群" : R.IsMember ? "已在群内"
            : R.HasPendingRequest ? "已申请" : R.IsPublic ? "＋ 加入" : "✉ 申请";
        public bool CanAct => !R.IsOwner && !R.IsMember && !R.HasPendingRequest;
        public Brush KindBrush => R.IsPublic
            ? UiPalette.Warm
            : UiPalette.Muted;
    }

    public SearchGroupDialog(SyncClient client)
    {
        InitializeComponent();
        _client = client;
        // 订阅搜索结果(Window 关闭时必走 Unloaded,成对订阅防泄漏)
        Loaded += (_, _) => _client.GroupSearchResultReceived += OnSearchResult;
        Unloaded += (_, _) => _client.GroupSearchResultReceived -= OnSearchResult;
        NumBox.Focus();
    }

    private void OnNumKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Search();
    }

    private void OnSearch(object sender, RoutedEventArgs e) => Search();

    /// <summary>输入即搜:清空时恢复默认提示并清掉候选;非空自动搜索。</summary>
    private void OnSearchBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (NumBox.Text.Trim().Length == 0)
        {
            ErrText.Visibility = Visibility.Collapsed;
            HintText.Text = "输入群名或群号搜索(群号在群聊列表和会话头部显示),点结果直接加入或申请。";
            ResultList.ItemsSource = null;
            return;
        }
        Search();
    }

    private void Search()
    {
        var keyword = NumBox.Text.Trim();
        ErrText.Visibility = Visibility.Collapsed;
        if (keyword.Length == 0)
        {
            HintText.Text = "输入群名或群号搜索";
            return;
        }
        HintText.Text = "搜索中...";
        _client.SearchGroup(keyword); // 结果走 OnSearchResult
    }

    private void OnSearchResult(List<GroupSearchResult> groups)
    {
        ErrText.Visibility = Visibility.Collapsed;
        if (groups.Count == 0)
        {
            HintText.Text = $"没有找到与「{NumBox.Text.Trim()}」匹配的群";
            ResultList.ItemsSource = null;
            return;
        }
        ResultList.ItemsSource = groups.Select(g => new GroupItem { R = g }).ToList();
        HintText.Text = $"找到 {groups.Count} 个群,点右侧按钮加入或申请。";
    }

    private void OnJoin(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: GroupItem item })
            return;
        ErrText.Visibility = Visibility.Collapsed;
        // 公共群直接加入;好友群发申请,同意后 groupUpdated 推送自动入群
        if (item.R.IsPublic)
        {
            _client.JoinGroup(item.R.GroupId);
            item.R.IsMember = true; // 本地置位,按钮立刻变"已在群内"
            HintText.Text = $"已加入「{item.R.Name}」,群聊已在左侧列表";
        }
        else
        {
            _client.SendGroupRequest(item.R.GroupId);
            item.R.HasPendingRequest = true; // 本地置位,按钮立刻变"已申请"
            HintText.Text = $"已向「{item.R.Name}」发送申请,群主/管理员审批后即可进群";
        }
        RefreshResult();
    }

    /// <summary>重新绑定列表,让被点那行的按钮态立即更新,防止重复操作。</summary>
    private void RefreshResult()
    {
        if (ResultList.ItemsSource is not List<GroupItem> items)
            return;
        ResultList.ItemsSource = null;
        ResultList.ItemsSource = items;
    }
}
