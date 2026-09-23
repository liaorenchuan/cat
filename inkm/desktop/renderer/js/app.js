// Inkm Desktop — 渲染进程 UI 逻辑
'use strict';

let isOnline = false;
let lastStatus = null;
let _serverBusy = false;
let _lastServerStatus = null; // 'online' | 'offline' | 'connecting' | 'unknown'

const TUNNELS = [
  { key:'cloudflare', icon:'☁️', bg:'#fce8e6', label:'Cloudflare' },
];

// ====== 状态映射 ======

function getStateInfo(ch) {
  if (!ch) return { text: '未知', cls: 'unknown', icon: '❓' };
  switch (ch.status) {
    case 'online':     return { text: '已连接',      cls: 'online',    icon: '✅' };
    case 'connecting': return { text: '连接中...',   cls: 'connecting', icon: '🔄' };
    case 'offline':    return { text: '未连接',      cls: 'offline',   icon: '⚫' };
    default:           return { text: '未知',        cls: 'unknown',   icon: '❓' };
  }
}

// ====== 渲染隧道列表（Cloudflare 卡片内嵌二维码） ======

let _lastTunnelHtml = '';
let _lastTunnelUrl = '';

function renderTunnels(status) {
  const list = document.getElementById('tunnelList');
  if (!status) return;
  const serverOnline = status.server?.status === 'online';
  let html = '';
  for (const t of TUNNELS) {
    const ch = status[t.key];
    if (!ch) continue;
    const info = getStateInfo(ch);
    const dot = ch.status === 'online' ? 'on' : ch.status === 'connecting' ? 'connecting' : 'off';
    const tunnelOnline = ch.status === 'online';
    const tunnelConnecting = ch.status === 'connecting';
    const url = (tunnelOnline && ch.url && ch.url !== '未连接') ? ch.url : null;

    const canConnect = serverOnline && !tunnelOnline && !tunnelConnecting && !_serverBusy;
    const canDisconnect = serverOnline && tunnelOnline && !_serverBusy;

    let errorHtml = '';
    if (ch.lastError && !tunnelOnline) {
      errorHtml = `<div class="ch-err">⚠️ ${ch.lastError}</div>`;
    }

    // 隧道在线时在卡片内嵌二维码
    let qrHtml = '';
    if (tunnelOnline && url) {
      qrHtml = `<div class="tunnel-qr-row">
        <div id="qrTunnel"></div>
        <div class="tunnel-qr-text">
          <div class="tunnel-qr-url">${url}</div>
          <div class="tunnel-qr-hint">🌐 不限距离，但延迟较大</div>
        </div>
      </div>`;
    }

    html += `<div class="ch-card" data-key="${t.key}">
      <div class="ch-top">
        <span class="ch-dot ${dot}"></span>
        <div class="ch-icon" style="background:${t.bg}">${t.icon}</div>
        <div class="ch-body" style="flex:1;min-width:0">
          <div class="ch-name">${t.label}</div>
          <div class="ch-state ch-${info.cls}">${info.icon} ${info.text}</div>
        </div>
        <div class="ch-actions">
          <button class="ch-btn ch-btn-connect" onclick="connectTunnel()" ${canConnect ? '' : 'disabled'}>连接</button>
          <button class="ch-btn ch-btn-disconnect" onclick="disconnectTunnel()" ${canDisconnect ? '' : 'disabled'}>断开</button>
        </div>
      </div>
      ${url ? `<div class="ch-url" onclick="copyUrl('${t.key}')" title="点击复制">🔗 ${url}</div>` : ''}
      ${errorHtml}
      ${qrHtml}
    </div>`;
  }
  if (html !== _lastTunnelHtml) {
    _lastTunnelHtml = html;
    list.innerHTML = html;
  }
  // 二维码重建（放在 DOM 更新后）
  const tch = status.cloudflare;
  const tunnelUrl = (tch?.status === 'online' && tch?.url && tch.url !== '未连接') ? tch.url : '';
  if (tunnelUrl !== _lastTunnelUrl) {
    _lastTunnelUrl = tunnelUrl;
    const container = document.getElementById('qrTunnel');
    if (container) {
      container.innerHTML = '';
      if (tunnelUrl) {
        try {
          new QRCode(container, {
            text: tunnelUrl, width: 100, height: 100,
            colorDark: '#000000', colorLight: '#ffffff',
            correctLevel: QRCode.CorrectLevel.M,
          });
        } catch (e) {}
      }
    }
  }
}

// ====== 更新整体 UI ======

function updateUI(status) {
  lastStatus = status;
  if (!status) return;
  renderTunnels(status);

  const sv = status.server;
  const newStatus = sv ? sv.status : 'unknown';
  if (newStatus !== _lastServerStatus) {
    _lastServerStatus = newStatus;
    const ring = document.getElementById('srvRing');
    const main = document.getElementById('srvMain');
    const urlEl = document.getElementById('srvUrl');
    const btn = document.getElementById('srvBtn');

    switch (newStatus) {
      case 'online':
        ring.className = 'srv-ring on';
        ring.textContent = '▶';
        main.textContent = '服务运行中';
        urlEl.textContent = status.lan?.url || 'http://localhost:3456';
        btn.className = 'srv-btn stop';
        btn.textContent = '■ 停止';
        btn.disabled = false;
        isOnline = true;
        _serverBusy = false;
        break;
      case 'connecting':
        ring.className = 'srv-ring connecting';
        ring.textContent = '🔄';
        main.textContent = '连接中...';
        urlEl.textContent = '';
        btn.className = 'srv-btn connecting';
        btn.textContent = '⏳ 启动中...';
        btn.disabled = true;
        isOnline = false;
        _serverBusy = true;
        break;
      default: // offline, unknown
        ring.className = 'srv-ring off';
        ring.textContent = '⏸';
        main.textContent = '服务未运行';
        urlEl.textContent = '';
        btn.className = 'srv-btn start';
        btn.textContent = '▶ 启动';
        btn.disabled = false;
        isOnline = false;
        _serverBusy = false;
        break;
    }
  }

  // 防火墙警告（服务器在线但防火墙规则缺失）
  if (newStatus === 'online' && status.firewallOk === false) {
    showFwWarning();
  } else if (newStatus !== 'online' || status.firewallOk !== false) {
    hideFwWarning();
  }
}

// ====== 防火墙警告 ======

function showFwWarning() {
  let el = document.getElementById('fwWarning');
  if (!el) {
    el = document.createElement('div');
    el.id = 'fwWarning';
    el.className = 'fw-warning';
    el.innerHTML = '⚠️ <b>防火墙未放行端口</b> — 手机可能无法通过局域网连接。<br>点击下方命令复制，在<b>管理员终端</b>中执行：<br><code id="fwCmd" style="cursor:pointer;background:rgba(0,0,0,.06);padding:3px 8px;border-radius:4px;margin-top:4px;display:inline-block;user-select:all;word-break:break-all;font-size:11px">netsh advfirewall firewall add rule name="Inkm 智能键鼠" dir=in action=allow protocol=TCP localport=3456</code>';
    const card = document.getElementById('srvCard');
    if (card) card.insertAdjacentElement('afterend', el);
    // 点击复制
    document.getElementById('fwCmd').addEventListener('click', function() {
      const cmd = this.textContent;
      window.electronAPI.copyText(cmd);
      this.style.background = 'rgba(16,185,129,.2)';
      this.textContent = '✅ 已复制！粘贴到管理员终端执行';
      setTimeout(() => {
        this.textContent = cmd;
        this.style.background = 'rgba(0,0,0,.06)';
      }, 2000);
    });
  }
  el.style.display = 'block';
}

function hideFwWarning() {
  const el = document.getElementById('fwWarning');
  if (el) el.style.display = 'none';
}

// ====== LAN 二维码（带缓存防抖）=====

let _lastLanUrl = '';

function renderLanQR(status) {
  const section = document.getElementById('qrLanSection');
  const urlEl = document.getElementById('qrLanUrl');
  if (!section) return;
  const serverOnline = status?.server?.status === 'online';
  const url = serverOnline ? (status.lan?.url || '') : '';
  section.style.display = url ? 'block' : 'none';
  if (urlEl) urlEl.textContent = url;
  if (url === _lastLanUrl) return;
  _lastLanUrl = url;
  const container = document.getElementById('qrLan');
  if (!container) return;
  container.innerHTML = '';
  if (url) {
    try {
      new QRCode(container, {
        text: url, width: 100, height: 100,
        colorDark: '#000000', colorLight: '#ffffff',
        correctLevel: QRCode.CorrectLevel.M,
      });
    } catch (e) {}
  }
}

// ====== IPC 监听 ======

window.electronAPI.onChannelStatus(status => {
  updateUI(status);
  renderLanQR(status);
});
window.electronAPI.channelStatus().then(status => {
  updateUI(status);
  renderLanQR(status);
});

// ====== 操作函数 ======

window.toggleServer = async function toggleServer() {
  if (_serverBusy) return;
  _serverBusy = true;
  const btn = document.getElementById('srvBtn');
  btn.disabled = true;
  const wasOnline = isOnline;
  btn.textContent = wasOnline ? '⏳ 停止中...' : '⏳ 启动中...';
  // 立即刷新隧道按钮状态（不等 2s 推送）
  if (lastStatus) renderTunnels(lastStatus);
  try {
    const result = await (wasOnline ? window.electronAPI.serverStop() : window.electronAPI.serverStart());
    if (result && result.status === 'starting') {
      showToast('⏳ 正在启动中，请稍候...');
      return; // 让下次状态推送更新 UI
    }
    if (result && result.status === 'failed') {
      _lastServerStatus = null; // 强制下次 updateUI 刷新
      showToast('⚠️ 操作失败，请重试');
    }
  } catch {
    _lastServerStatus = null;
    showToast('❌ 操作异常');
  }
};

window.connectTunnel = async function connectTunnel() {
  showToast('🔄 正在连接 Cloudflare...');
  try {
    await window.electronAPI.channelConnect();
    showToast('✅ 连接指令已发出');
  } catch { showToast('❌ 连接失败'); }
};

window.disconnectTunnel = async function disconnectTunnel() {
  showToast('⏹ 正在断开 Cloudflare...');
  try {
    await window.electronAPI.channelDisconnect();
    showToast('⏹ 已断开');
  } catch { showToast('❌ 断开失败'); }
};

window.copyUrl = async function copyUrl(key) {
  let url = '';
  if (!key) {
    url = lastStatus?.lan?.url || '';
  } else {
    url = lastStatus?.[key]?.url || '';
  }
  if (!url || url === '未连接') return;
  try {
    await window.electronAPI.copyText(url);
    showToast('📋 已复制');
  } catch { /* 忽略 */ }
};

function showToast(msg) {
  const t = document.getElementById('toast');
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(t._hide);
  t._hide = setTimeout(() => t.classList.remove('show'), 2000);
}
