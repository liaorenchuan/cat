using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QPet.Core;

namespace QPet.Wpf.Dialogs;

/// <summary>选好友建群(邀请制):群名 + 好友多选,建群后对选中好友发邀请,同意后进群。</summary>
public partial class CreateGroupDialog : Window
{
    private readonly SyncClient _client;
    private readonly List<CheckBox> _boxes = new();

    public CreateGroupDialog(SyncClient client, List<FriendInfo> friends)
    {
        InitializeComponent();
        _client = client;

        foreach (var f in friends)
        {
            var box = new CheckBox
            {
                Content = $"  {(f.Remark.Length > 0 ? f.Remark : f.Nickname)}",
                FontSize = 13,
                Foreground = System.Windows.Media.Brushes.DarkGoldenrod,
                Margin = new Thickness(2, 5, 0, 5),
                Tag = f.UserId,
            };
            _boxes.Add(box);
            FriendPanel.Children.Add(box);
        }
        if (_boxes.Count == 0)
        {
            FriendPanel.Children.Add(new TextBlock
            {
                Text = "还没有好友,先添加好友再建群",
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.Gray,
            });
        }
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Create();
    }

    private void OnCreate(object sender, RoutedEventArgs e) => Create();

    private void Create()
    {
        var name = NameBox.Text.Trim();
        var selected = _boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        ErrorText.Visibility = Visibility.Collapsed;
        if (name.Length == 0)
        {
            ErrorText.Text = "请输入群名称";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (selected.Count == 0)
        {
            ErrorText.Text = "至少选择一个好友";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        // 邀请制建群:新群初始只有群主 1 人,对选中好友发出邀请,同意后进群;
        // 群列表即时出现 1 人群,对方同意后 groupUpdated 推送刷新
        _client.CreateGroup(name, selected);
        ErrorText.Text = "已创建群聊,已向所选好友发出邀请,同意后进群";
        ErrorText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x9E, 0x44));
        ErrorText.Visibility = Visibility.Visible;
        // 稍后自动关闭(让用户看到成功提示)
        var dlg = this;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) => { timer.Stop(); dlg.Close(); };
        timer.Start();
    }
}
