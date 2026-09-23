using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QPet.Core;
using QPet.Wpf.Views;

namespace QPet.Wpf.Dialogs;

/// <summary>按账号/昵称搜索用户并发送好友申请(请求-响应式:发搜索帧,结果事件回来)。</summary>
public partial class AddFriendDialog : Window
{
    private readonly SyncClient _client;

    public AddFriendDialog(SyncClient client)
    {
        InitializeComponent();
        _client = client;
        Loaded += (_, _) => _client.SearchResultsReceived += OnSearchResults;
        Closed += (_, _) => _client.SearchResultsReceived -= OnSearchResults;
    }

    private sealed class ResultItem
    {
        public UserSearchResult R = null!;
        // WPF 绑定只认属性
        public string Nickname => R.Nickname;
        public string AccountHint => $"账号: {R.Account}";
        public string BtnText => R.IsFriend ? "已是好友" : R.HasPendingRequest ? "已申请" : "＋ 添加";
        public bool CanAdd => !R.IsFriend && !R.HasPendingRequest;
        // 头像与好友列表同款:首字 + 账号哈希固定配色
        public string AvatarChar => R.Nickname.Length > 0 ? R.Nickname.Substring(0, 1) : "?";
        public Brush AvatarBrush => ChatView.AvatarBrushFor(R.UserId);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Search();
    }

    /// <summary>输入即搜(实时):清空时恢复默认提示并清掉结果;非空自动搜索。</summary>
    private void OnSearchBoxChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length == 0)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            HintText.Text = "输入对方账号或昵称搜索,然后发送好友申请。";
            ResultList.ItemsSource = null;
            return;
        }
        Search();
    }

    private void OnSearch(object sender, RoutedEventArgs e) => Search();

    private void Search()
    {
        var keyword = SearchBox.Text.Trim();
        ErrorText.Visibility = Visibility.Collapsed;
        if (keyword.Length == 0)
        {
            ErrorText.Text = "请输入账号或昵称";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        HintText.Text = "搜索中...";
        _client.SearchFriends(keyword);
    }

    private void OnSearchResults(List<UserSearchResult> results)
    {
        ResultList.ItemsSource = results.Select(r => new ResultItem { R = r }).ToList();
        HintText.Text = ResultList.Items.Count == 0 ? "没有找到匹配的用户" : "找到 " + ResultList.Items.Count + " 个用户";
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ResultItem item })
            return;
        ErrorText.Visibility = Visibility.Collapsed;
        _client.SendFriendRequest(item.R.UserId, "");
        HintText.Text = $"已向 {item.R.Nickname} 发送申请,等待对方同意";
        Search(); // 重新搜索,刷新按钮状态为"已申请"
    }
}
