using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QPet.Core;
using QPet.Ui;
using QPet.Wpf.Dialogs;

namespace QPet.Wpf.Views;
/// <summary>ChatView 分部:会话列表与页签(重建/未读红点/页签切换/会话切换)。</summary>
public partial class ChatView
{
    // ---- 消息页:会话列表 ----

    /// <summary>重建会话列表(好友+群合并,按最后消息时间倒序;无消息会话排最后,群在前)。</summary>
    private void RefreshConvList()
    {
        _convs.Clear();
        // 公共群聊固定最前
        foreach (var g in _groups.OrderBy(g => g.GroupId == "public" ? 0 : 1))
        {
            var key = $"{Protocol.ConvKeyGroup}{g.GroupId}";
            if (_hiddenConvs.Contains(key)) continue; // 已删除的会话:列表不显示(新消息来才恢复)
            var last = _lastPreview.GetValueOrDefault(key);
            var title = GroupDisplayName(g);
            _convs.Add(new ConvEntry
            {
                ConvType = 1,
                ConvId = g.GroupId,
                Title = title,
                Preview = last is null ? $"成员 {g.Members.Count} 人" : PreviewText(last),
                TimeText = last is null ? "" : ConvTimeText(last),
                Unread = _unread.GetValueOrDefault(key),
                LastTicks = last?.Time.ToLocalTime().Ticks ?? 0,
                AvatarText = "👥",
                AvatarBrush = GroupAvatarBrush,
            });
        }
        foreach (var f in _friends)
        {
            var key = $"{Protocol.ConvKeyPrivate}{f.UserId}";
            if (_hiddenConvs.Contains(key)) continue; // 已删除的会话:列表不显示(新消息来才恢复)
            var last = _lastPreview.GetValueOrDefault(key);
            var name = FriendDisplayName(f);
            _convs.Add(new ConvEntry
            {
                ConvType = 0,
                ConvId = f.UserId,
                Title = name,
                Preview = last is null ? "暂无消息" : PreviewText(last),
                TimeText = last is null ? "" : ConvTimeText(last),
                Unread = _unread.GetValueOrDefault(key),
                LastTicks = last?.Time.ToLocalTime().Ticks ?? 0,
                Online = f.Online && _client.IsConnected, // 断网/离线:全部置灰
                AvatarText = name.Length > 0 ? name.Substring(0, 1) : "?",
                AvatarBrush = AvatarBrushFor(f.UserId),
            });
        }
        // 新实例:同引用赋值不触发刷新(见 RefreshFriendList 注释)
        ConvEmptyText.Visibility = _convs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _convListRefreshing = true; // 恢复选中会触发 SelectionChanged → OnConvSelected,期间重入忽略
        try
        {
            ConvList.ItemsSource = _convs.OrderByDescending(c => c.LastTicks).ToList();
            RestoreConvSelection();
        }
        finally
        {
            _convListRefreshing = false;
        }
        RefreshMsgTabBadge(); // 消息页签红点 = 全部会话未读总数
    }

    /// <summary>消息页签红点:所有会话未读之和(>99 显示 99+)。</summary>
    private void RefreshMsgTabBadge()
    {
        var n = 0;
        foreach (var v in _unread.Values) n += v;
        MsgTabBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        MsgTabBadgeText.Text = n > 99 ? "99+" : n.ToString();
    }

    /// <summary>列表预览文案:群聊别人发的带昵称前缀,自己发的带"我:",系统消息原文。
    /// 只取第一行(多行消息截断到首个换行),超长由 TextTrimming 省略,会话栏不会被撑宽/撑高。</summary>
    private string PreviewText(ChatMessage m)
    {
        string prefix;
        if (m.Type == 1)
            prefix = ""; // 系统消息文本自带语境("「X」加入了群聊")
        else if (m.ConvType == 1 && m.FromUserId != _me && m.FromName.Length > 0)
            prefix = m.FromName + ": ";
        else if (m.FromUserId == _me)
            prefix = "我: ";
        else
            prefix = "";
        var text = m.Text;
        var nl = text.IndexOf('\n');
        if (nl >= 0)
            text = text.Substring(0, nl).TrimEnd(); // 多行消息只留第一行
        if (text.Length > 10)
            text = text.Substring(0, 10); // 只显示前 10 个字符
        return prefix + text;
    }

    /// <summary>会话列表时间:今天 HH:mm / 昨天 / MM-dd。</summary>
    private static string ConvTimeText(ChatMessage m)
    {
        var t = m.Time.ToLocalTime();
        var today = DateTime.Today;
        if (t.Date == today)
            return t.ToString("HH:mm");
        if (t.Date == today.AddDays(-1))
            return "昨天";
        return t.ToString("MM-dd");
    }

    /// <summary>刷新列表后恢复当前会话的选中状态。
    /// 已是目标条目不重复赋值,避免 SelectionChanged → SelectConv 重入(收到消息刷新列表时)。</summary>
    private void RestoreConvSelection()
    {
        if (!_convOpen)
        {
            // 未打开会话:列表不保持选中(登录/关闭会话后刷新列表不自动重开会话)
            if (ConvList.SelectedItem is not null)
                ConvList.SelectedItem = null;
            return;
        }
        if (ConvList.SelectedItem is ConvEntry cur && cur.ConvType == _convType && cur.ConvId == _convId)
            return;
        ConvList.SelectedItem = _convs.FirstOrDefault(c => c.ConvType == _convType && c.ConvId == _convId);
    }


    // ---- 未读红点(服务器权威:welcome 全量 + unreadUpdate 实时,本端只做显示) ----

    /// <summary>一次历史补拉完成(首次全量/重连增量):重建各会话预览;
    /// 历史期间消息只累积,这里一次性重建当前会话视图(避免逐条刷新批量卡顿)。
    /// 未读红点不重算:由服务器 welcome/推送权威下发,历史补拉不覆盖。</summary>
    private void OnHistoryLoaded()
    {
        _historyDone = true;
        RebuildPreview();
        RebuildView();
        // 历史期间只累积没上报:补拉完成补一次已读上报(当前会话,同时服务器把该会话未读归零);
        // 未打开会话不上报(右侧空白,不算已读)
        if (_convOpen)
            _client.SendRead(_convType, _convId, _convMaxSeq.GetValueOrDefault($"{_convType}:{_convId}"));
    }

    /// <summary>重算各会话最后一条消息(预览/时间),不碰未读数。</summary>
    private void RebuildPreview()
    {
        var lastByConv = new Dictionary<string, ChatMessage>();
        foreach (var m in _all)
        {
            var key = $"{m.ConvType}:{m.ConvId}";
            lastByConv[key] = m;
        }
        _lastPreview.Clear();
        foreach (var (key, m) in lastByConv)
            _lastPreview[key] = m;
        RefreshConvList();
    }

    /// <summary>未读红点推送(消息送达 +1 / 打开会话归零):刷新会话红点 + 页签求和。</summary>
    private void OnUnreadUpdated(string convKey, int count)
    {
        if (_hiddenConvs.Contains(convKey)) return; // 已删除会话:红点不显示(也不进页签求和,避免虚高)
        _unread[convKey] = count;
        RefreshConvList();
        RefreshMsgTabBadge();
    }

    // ---- 页签切换 ----

    private int _currentTab; // 0=消息 1=好友 2=群聊 3=宠物(切走再回保持)

    private void ShowTab(int tab)
    {
        _currentTab = tab;
        MsgPanel.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        FriendPanel.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        GroupPanel.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        PetHouseHost.Visibility = tab == 3 ? Visibility.Visible : Visibility.Collapsed;
        SetTabActive(TabMsgButton, tab == 0);
        SetTabActive(TabFriendButton, tab == 1);
        SetTabActive(TabGroupButton, tab == 2);
        SetTabActive(TabPetButton, tab == 3);
        if (tab == 0)
        {
            ScrollToEnd(); // 切回消息页:滚到底看到最新
            RefreshMsgTabBadge();
        }
    }

    private static void SetTabActive(Button b, bool active)
    {
        // 选中:白字 + 橙色渐变胶囊(与主按钮同款,层级一眼分清);未选中:奶油橙底 + 浅棕灰字
        b.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        b.Foreground = active ? Brushes.White : UiPalette.Brown;
        b.Background = active
            ? new LinearGradientBrush(UiPalette.WarmTopColor, UiPalette.WarmColor, 90)
            : new SolidColorBrush(Color.FromRgb(0xFD, 0xEB, 0xD4));
    }

    private void OnTabMessage(object sender, RoutedEventArgs e) => ShowTab(0);
    private void OnTabFriends(object sender, RoutedEventArgs e) => ShowTab(1);
    private void OnTabGroups(object sender, RoutedEventArgs e) => ShowTab(2);
    private void OnTabPet(object sender, RoutedEventArgs e) => ShowTab(3);

    /// <summary>悬浮窗"回家"切回宠物页签(App.GoHomeFromFloating 调用)。</summary>
    internal void ShowPetTab() => ShowTab(3);

    /// <summary>宠物页签实例(App 悬浮窗外出/回家联动用)。</summary>
    internal PetHouseView PetHouseHostView => PetHouseHost;

    // ---- 会话切换 ----

    /// <summary>右键菜单"删除会话":从消息列表删除该会话(本地隐藏);
    /// 对方再发消息时自动重新出现。不删服务器记录/聊天记录,只是左侧列表不显示。</summary>
    private void OnDeleteConvMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConvEntry c)
            return;
        var key = $"{c.ConvType}:{c.ConvId}";
        _hiddenConvs.Add(key);
        _unread.Remove(key);
        _lastPreview.Remove(key);
        _convMaxSeq.Remove(key);
        RefreshConvList(); // 条目消失 + 页签徽章重算
    }

    private void OnConvSelected(object sender, SelectionChangedEventArgs e)
    {
        // RefreshConvList 重建列表时恢复选中会触发本事件(ItemsSource 换新实例,
        // SelectedItem 引用失效被清空再恢复):重入忽略,避免 SelectConv → RefreshConvList 死循环(栈溢出)
        if (_convListRefreshing)
            return;
        if (ConvList.SelectedItem is ConvEntry c)
            SelectConv(c.ConvType, c.ConvId);
    }

    /// <summary>好友页点击好友 → 打开好友详情页(昵称/备注/账号/删除/发消息)。</summary>
    private void OnFriendSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FriendList.SelectedItem is not FriendItem f)
            return;
        FriendList.SelectedItem = null; // 详情页从列表点开,不保留选中(避免重入)
        var info = _friends.FirstOrDefault(x => x.UserId == f.UserId);
        if (info is null)
            return;
        var dlg = new FriendDetailDialog(_client, info,
            onOpenChat: () => { SelectConv(0, info.UserId); ShowTab(0); })
        { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        RefreshFriendList();
    }

    /// <summary>群聊页点击群 → 打开群详情页(群名/备注/群号/退出/发消息)。</summary>
    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupItem g)
            return;
        GroupList.SelectedItem = null;
        var info = _groups.FirstOrDefault(x => x.GroupId == g.GroupId);
        if (info is null)
            return;
        var dlg = new GroupDetailDialog(_client, info,
            onOpenChat: () => { SelectConv(1, info.GroupId); ShowTab(0); })
        { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        RefreshGroupList();
        RefreshConvList();
        // 详情页里可能改了群备注:刷新消息栏标题 + 备注按钮文字(当前会话是群聊时)
        if (_convType == 1)
        {
            var grp = _groups.FirstOrDefault(x => x.GroupId == _convId);
            if (grp is not null)
                ConvTitleText.Text = GroupDisplayName(grp);
            UpdateGroupRemarkButton();
        }
    }

    private void SelectConv(int convType, string convId)
    {
        _convOpen = true;
        _convType = convType;
        _convId = convId;
        StatusText.Text = "";
        MsgInput.IsEnabled = true;

        // 打开会话:未读本地立即清零(服务器由 SendRead 归零并回推 0,幂等)
        var key = $"{convType}:{convId}";
        _unread[key] = 0;
        RefreshConvList(); // 立即重画会话列表,清除已查看会话的红点(不清则徽章残留到下次刷新)
        RefreshConvHeader();
        RebuildView();
        // 打开会话 = 已读本会话全部消息:上报游标,对方看到"已读"
        _client.SendRead(convType, convId, _convMaxSeq.GetValueOrDefault(key));
        // 单击打开会话:输入栏默认聚焦,直接打字,Enter 即发送
        Dispatcher.BeginInvoke(DispatcherPriority.Background, MsgInput.Focus);
    }

    /// <summary>关闭当前会话(登录初始 / 不选中任何会话):右侧空白,头部清空,输入禁用。
    /// 点左侧会话条目才重新打开。</summary>
    private void CloseConv()
    {
        _convOpen = false;
        _convId = "";
        StatusText.Text = "";
        MsgInput.IsEnabled = false;
        ConvTitleText.Text = "";
        ConvSubText.Text = "";
        ConvDot.Visibility = Visibility.Collapsed; // 头部状态点随会话关闭隐藏
        MembersButton.Visibility = Visibility.Collapsed;
        DeleteFriendButton.Visibility = Visibility.Collapsed;
        ConvActionButton.Visibility = Visibility.Collapsed;
        GroupRemarkButton.Visibility = Visibility.Collapsed;
        LeaveGroupButton.Visibility = Visibility.Collapsed;
        RebuildView(); // 清空消息区("" 不匹配任何消息)
    }

    /// <summary>会话头部(标题/子标题/按钮)按当前会话刷新。
    /// 登录初期 welcome 快照未到就 SelectConv 时,头部是空的;快照到达后补刷新。</summary>
    private void RefreshConvHeader()
    {
        if (_convType == 0)
        {
            var f = _friends.FirstOrDefault(x => x.UserId == _convId);
            ConvTitleText.Text = f is null ? "" : FriendDisplayName(f);
            ConvSubText.Text = "私聊";
            // 私聊头部状态点:连接且好友在线才绿,离线/断网一律灰
            var online = _client.IsConnected && !_client.IsOffline && f?.Online == true;
            ConvDot.Fill = online ? UiPalette.Green : Brushes.Silver;
            ConvDot.Visibility = Visibility.Visible;
            MembersButton.Visibility = Visibility.Collapsed;
            DeleteFriendButton.Visibility = Visibility.Visible;
            ConvActionButton.Content = "✏ 备注";
            ConvActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            var g = _groups.FirstOrDefault(x => x.GroupId == _convId);
            if (g is not null)
            {
                ConvTitleText.Text = GroupDisplayName(g);
                ConvSubText.Text = g.Num.Length > 0
                    ? $"群号 {g.Num} · 成员 {g.Members.Count} 人"
                    : $"成员 {g.Members.Count} 人";
                // 群聊不显示在线状态点(成员在线数依赖实时推送,离线无意义)
                ConvDot.Visibility = Visibility.Collapsed;
                // 群会话可查看成员;公共群聊不可改名、不可退群(默认群,人人都在)
                MembersButton.Visibility = Visibility.Visible;
                DeleteFriendButton.Visibility = Visibility.Collapsed;
                LeaveGroupButton.Visibility = g.GroupId == "public"
                    ? Visibility.Collapsed : Visibility.Visible;
                ConvActionButton.Content = "✏ 改名";
                ConvActionButton.Visibility = g.GroupId == "public"
                    ? Visibility.Collapsed : Visibility.Visible;
                // 群备注按钮:非公共群可见,文字显示当前备注名
                GroupRemarkButton.Visibility = g.GroupId == "public"
                    ? Visibility.Collapsed : Visibility.Visible;
                UpdateGroupRemarkButton();
            }
        }
    }
}
