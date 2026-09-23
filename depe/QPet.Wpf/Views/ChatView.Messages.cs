using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QPet.Core;
using QPet.Ui;

namespace QPet.Wpf.Views;
/// <summary>ChatView 分部:消息渲染与已读回执(收消息/重建视图/追加/日期栏/滚动)。</summary>
public partial class ChatView
{
    // ---- 消息 ----

    /// <summary>收到一条消息:实时推送或全量历史补拉(已在 UI 线程,Seq 去重)。</summary>
    private void OnMessage(ChatMessage msg)
    {
        if (!_seen.Add(msg.Seq))
            return;
        _all.Add(msg);
        // 与服务器消息上限对齐,防长期挂机内存膨胀
        if (_all.Count > MaxMessages)
        {
            var dropped = _all[0];
            _all.RemoveAt(0);
            _seen.Remove(dropped.Seq); // 同时放出被逐出消息的 Seq,防 _seen 无限增长
        }
        var key = $"{msg.ConvType}:{msg.ConvId}";
        if (msg.Seq > _convMaxSeq.GetValueOrDefault(key))
            _convMaxSeq[key] = msg.Seq; // 各会话最大 Seq 保持最新(SelectConv 清零用)

        if (msg.ConvType == _convType && msg.ConvId == _convId)
        {
            // 当前会话:只追加不计数(已在看,无需红点;未读由服务器回推 0)
            AppendToView(msg);
            // 历史补拉期间只累积(O(1) 每条,不碰 ItemsSource),完成时 RebuildView 一次;
            // 之后实时消息每条刷新(虚拟化下重建只渲染可见气泡,开销小)
            if (_historyDone)
            {
                UpdateReadMarkers(); // 新消息(含自己发的)重算已读/未读标记
                RefreshMsgList();
                // 正在看 = 新消息立即已读:上报游标,对方实时看到"已读",
                // 服务器同时把该会话未读归零并回推(幂等)
                _client.SendRead(_convType, _convId, msg.Seq);
                // 预览照常更新(自己发的显示"我: xxx"),会话列表重排
                _hiddenConvs.Remove(key); // 已删除会话:有新消息(含自己发的)即重新出现
                _lastPreview[key] = msg;
                RefreshConvList();
            }
        }
        else
        {
            // 非当前会话:未读由服务器计数并推 unreadUpdate(别人消息/系统消息 +1),
            // 本端只更新预览;历史补拉期间也只累积,完成时 RebuildPreview 统一重建
            // 已删除会话:对方/系统来新消息即重新出现在列表
            if (_historyDone)
                _hiddenConvs.Remove(key);
            _lastPreview[key] = msg;
            if (_historyDone)
                RefreshConvList(); // 会话列表(预览/时间)重排
        }
    }

    /// <summary>当前会话视图整体重建(切会话/群更新),按时间分组规则逐条追加。</summary>
    private void RebuildView()
    {
        _view.Clear();
        _viewLastMsg = null;
        foreach (var m in _all)
        {
            if (m.ConvType == _convType && m.ConvId == _convId)
                AppendToView(m);
        }
        UpdateReadMarkers();
        RefreshMsgList();
    }

    /// <summary>消息列表刷新(重赋 ItemsSource 新实例;虚拟化下只重建可见容器,批量/增量都便宜)。</summary>
    private void RefreshMsgList()
    {
        MsgList.ItemsSource = null;
        MsgList.ItemsSource = _view;
        ScrollToEnd();
    }

    /// <summary>追加一条消息到当前视图:跨天先插日期栏,时间在气泡内(昵称下方/内容下方)。
    /// 维护 _viewLastMsg 游标,每条 O(1)(历史批量到达时旧版逐条反向扫描是卡顿主因)。</summary>
    private void AppendToView(ChatMessage msg)
    {
        var t = msg.Time.ToLocalTime();
        if (_viewLastMsg is null || _viewLastMsg.Time.ToLocalTime().Date != t.Date)
            _view.Add(new DateBar { Text = DateBarText(t.Date) }); // 跨天:日期栏
        // 状态点:我的点 = 连接时绿(离线/断网灰);对方点 = 仅私聊,按好友在线
        var mine = msg.FromUserId == _me;
        var connected = _client.IsConnected && !_client.IsOffline;
        var peerOnline = msg.ConvType == 0
            && _friends.FirstOrDefault(x => x.UserId == msg.FromUserId)?.Online == true;
        _view.Add(new MsgItem
        {
            Msg = msg,
            IsMine = mine,
            MyDotBrush = connected ? UiPalette.Green : Brushes.Silver,
            PeerDotBrush = connected && peerOnline ? UiPalette.Green : Brushes.Silver,
            ShowPeerDot = msg.ConvType == 0,
        });
        _viewLastMsg = msg;
    }

    /// <summary>重算已读/未读标记:每条自己的消息都标——已读(有回执)显示已读文案,未读显示"未读"。</summary>
    private void UpdateReadMarkers()
    {
        foreach (var item in _view)
        {
            if (item is not MsgItem m || !m.IsMine)
                continue;
            var text = ReadTextFor(m.Msg);
            m.ShowRead = true;
            m.ReadText = text.Length > 0 ? text : "未读";
        }
    }

    /// <summary>一条我的消息的已读文案:私聊 = 对方已读(游标 ≥ 该消息);群聊 = "已读 N/总"(N = 已读的其他成员数)。</summary>
    private string ReadTextFor(ChatMessage msg)
    {
        var key = $"{msg.ConvType}:{msg.ConvId}";
        if (!_convReadSeq.TryGetValue(key, out var readers))
            return "";
        if (msg.ConvType == 0)
        {
            // 私聊:读者 = 对方(会话 ID),游标 ≥ 该消息 Seq 即已读
            return readers.GetValueOrDefault(msg.ConvId) >= msg.Seq ? "已读" : "";
        }
        // 群聊:统计其他成员里游标 ≥ 该消息 Seq 的人数(成员数取当前群快照)
        var group = _groups.FirstOrDefault(g => g.GroupId == msg.ConvId);
        if (group is null || group.Members.Count <= 1)
            return "";
        var count = group.Members.Count(m => m.UserId != _me
            && readers.GetValueOrDefault(m.UserId) >= msg.Seq);
        return count > 0 ? $"已读 {count}/{group.Members.Count - 1}" : "";
    }

    /// <summary>收到已读回执:会话中某人已读到 seq(我的消息被对方读了)。正在看该会话 → 重算已读标记。</summary>
    private void OnRead(ReadReceipt r)
    {
        var key = $"{r.ConvType}:{r.ConvId}";
        if (!_convReadSeq.TryGetValue(key, out var readers))
            _convReadSeq[key] = readers = new Dictionary<string, long>();
        if (r.Seq <= readers.GetValueOrDefault(r.FromUserId))
            return; // 过期回执(游标只进不退)
        readers[r.FromUserId] = r.Seq;
        if (r.ConvType == _convType && r.ConvId == _convId)
        {
            UpdateReadMarkers();
            RefreshMsgList();
        }
    }

    /// <summary>welcome 快照的已读游标(ReadStatesReceived,与 ApplySnapshot 双保险,幂等)。</summary>
    private void OnReadStates(List<ReadState> states)
    {
        foreach (var s in states)
        {
            var key = $"{s.ConvType}:{s.ConvId}";
            if (!_convReadSeq.TryGetValue(key, out var readers))
                _convReadSeq[key] = readers = new Dictionary<string, long>();
            if (s.Seq > readers.GetValueOrDefault(s.ReaderUserId))
                readers[s.ReaderUserId] = s.Seq;
        }
    }

    /// <summary>日期栏文案:今天 / 昨天 / yyyy年M月d日 星期x。</summary>
    private static string DateBarText(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)
            return "今天";
        if (date == today.AddDays(-1))
            return "昨天";
        var week = new[] { "日", "一", "二", "三", "四", "五", "六" }[(int)date.DayOfWeek];
        return $"{date.Year}年{date.Month}月{date.Day}日 星期{week}";
    }

    private void ScrollToEnd() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, MsgScroll.ScrollToEnd);

}
