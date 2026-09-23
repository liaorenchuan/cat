// Inkm — 主程序（触控板 + 文字输入 + 连接检测）
'use strict';

// ====== DOM 快捷引用 ======
const $ = id => document.getElementById(id);

// ====== 全局状态 ======
let isInputMode = false;
let sending = false;
let connected = false;

// ====== 背景主题 ======
var tpIdx = 0, inputIdx = 0;
var tpThemes = [
  {n:'① 星空', bg:'linear-gradient(135deg,#667eea,#764ba2)'},
  {n:'③ 网格', bg:'linear-gradient(90deg,rgba(0,0,0,.05) 1px,transparent 0),linear-gradient(rgba(0,0,0,.05) 1px,transparent 0);background-size:20px 20px'},
  {n:'⑤ 极光', bg:'linear-gradient(135deg,#ff9a9e 0%,#fecfef 50%,#fdfcfb 100%)'},
  {n:'⑥ 水波纹', bg:'radial-gradient(ellipse at 30% 50%,rgba(80,160,255,.25) 0%,transparent 50%),radial-gradient(ellipse at 70% 50%,rgba(255,100,150,.2) 0%,transparent 50%),radial-gradient(ellipse at 50% 20%,rgba(255,200,50,.15) 0%,transparent 40%)'},
  {n:'⑦ 波点', bg:'radial-gradient(rgba(80,70,60,.12) 1.5px,transparent 0);background-size:16px 16px'},
  {n:'⑨ 色环', bg:'conic-gradient(from 0deg,#ff6b6b,#ffd93d,#6bcb77,#4d96ff,#ff6b6b)'},
  {n:'⑩ 斜纹', bg:'repeating-linear-gradient(45deg,transparent,transparent 8px,rgba(0,0,0,.04) 8px,rgba(0,0,0,.04) 10px)'},
  {n:'⑪ 深海', bg:'linear-gradient(135deg,#1a1a2e 0%,#16213e 50%,#0f3460 100%)'},
  {n:'⑫ 深空', bg:'linear-gradient(135deg,rgba(255,255,255,.08) 0%,rgba(255,255,255,.02) 100%),radial-gradient(circle at 20% 30%,rgba(255,200,100,.08) 0%,transparent 40%),radial-gradient(circle at 80% 70%,rgba(100,200,255,.08) 0%,transparent 40%),#2a2a3a'},
];
var inputThemes = [
  {n:'亮白', bg:'rgba(255,255,255,.5)'},
  {n:'暖黄', bg:'linear-gradient(135deg,rgba(250,245,235,.7),rgba(240,230,215,.6))'},
  {n:'星空', bg:'linear-gradient(135deg,#667eea,#764ba2);color:#fff'},
  {n:'深海', bg:'linear-gradient(135deg,#1a1a2e 0%,#16213e 50%,#0f3460 100%);color:#fff'},
  {n:'深空', bg:'linear-gradient(135deg,rgba(255,255,255,.08) 0%,rgba(255,255,255,.02) 100%),radial-gradient(circle at 20% 30%,rgba(255,200,100,.08) 0%,transparent 40%),radial-gradient(circle at 80% 70%,rgba(100,200,255,.08) 0%,transparent 40%),#2a2a3a;color:#fff'},
];

function applyTheme(el, theme) {
  var bg = theme.bg;
  var color = '';
  var m = bg.match(/;color:(#[^;]+|[\w]+)/);
  if (m) { color = m[1]; bg = bg.replace(/;color:[^;]+/, ''); }
  el.style.background = '';
  el.style.backgroundImage = '';
  var parts = bg.split(';background-size:');
  el.style.background = parts[0];
  if (parts[1]) el.style.backgroundSize = parts[1];
  if (color) el.style.color = color;
}

function cycleTheme() {
  if (isInputMode) {
    inputIdx = (inputIdx + 1) % inputThemes.length;
    var ta = document.getElementById('ta');
    if (ta) applyTheme(ta, inputThemes[inputIdx]);
  } else {
    tpIdx = (tpIdx + 1) % tpThemes.length;
    var tp = document.getElementById('touchpad');
    if (tp) applyTheme(tp, tpThemes[tpIdx]);
  }
}

// ====== DOM 元素 ======
const toggleBtn = $('mode-toggle');
const pt = $('pt');
const inputView = $('input-view');
const ta = $('ta');
const st = $('in-st');
const pad = $('touchpad');
const hint = pad ? pad.querySelector('.hint') : null;

// ====== 通用 API 请求 ======
function api(path, body) {
  if (!connected) return;
  fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  }).catch(() => {});
}

// ====== 模式切换 ======
window.toggleMode = function toggleMode() {
  isInputMode = !isInputMode;
  pt.classList.toggle('hidden', isInputMode);
  inputView.classList.toggle('active', isInputMode);
  toggleBtn.innerHTML = isInputMode ? '🖱️' : '⌨️';
  (isInputMode ? ta.focus : ta.blur)();
  if (!isInputMode) calibrate();
  updateTitle();
};
setTimeout(() => calibrate(), 500);

// 更新标题
function updateTitle() {
  var title = document.getElementById('header-title');
  if (title) title.textContent = isInputMode ? '⌨️ 输入框' : '🖱️ 触控板';
}
updateTitle();
setInterval(updateTitle, 200);

// ====== 悬浮按钮：手指拖动 + 点击切换 ======
(function() {
  var fab = document.getElementById('mode-toggle');
  if (!fab) return;
  var dragging = false, startX, startY, origX, origY;

  // 初始位置
  var sw = window.innerWidth, sh = window.innerHeight;
  fab.style.left = (sw - 60) + 'px';
  fab.style.top = Math.round((sh - 44) / 2) + 'px';

  fab.addEventListener('touchstart', function(e) {
    dragging = false;
    var t = e.touches[0];
    startX = t.clientX;
    startY = t.clientY;
    origX = fab.getBoundingClientRect().left;
    origY = fab.getBoundingClientRect().top;
  }, {passive: true});

  fab.addEventListener('touchmove', function(e) {
    var t = e.touches[0];
    var dx = t.clientX - startX;
    var dy = t.clientY - startY;
    if (Math.abs(dx) > 5 || Math.abs(dy) > 5) dragging = true;
    if (!dragging) return;
    e.preventDefault();
    var sw2 = window.innerWidth, sh2 = window.innerHeight;
    fab.style.left = Math.max(0, Math.min(sw2 - 60, origX + dx)) + 'px';
    fab.style.top = Math.max(0, Math.min(sh2 - 84, origY + dy)) + 'px';
  }, {passive: false});

  fab.addEventListener('touchend', function(e) {
    if (dragging) {
      var sw2 = window.innerWidth;
      var cur = parseInt(fab.style.left) || 0;
      fab.style.left = (cur < sw2 / 2) ? '0px' : (sw2 - 60) + 'px';
      dragging = false;
      e.preventDefault();
    }
  }, {passive: false});

  // 桌面鼠标
  fab.addEventListener('mousedown', function(e) {
    dragging = false;
    startX = e.clientX;
    startY = e.clientY;
    origX = fab.getBoundingClientRect().left;
    origY = fab.getBoundingClientRect().top;

    function mm(e2) {
      var dx = e2.clientX - startX;
      var dy = e2.clientY - startY;
      if (Math.abs(dx) > 5 || Math.abs(dy) > 5) dragging = true;
      if (!dragging) return;
      var sw2 = window.innerWidth, sh2 = window.innerHeight;
      fab.style.left = Math.max(0, Math.min(sw2 - 60, origX + dx)) + 'px';
      fab.style.top = Math.max(0, Math.min(sh2 - 84, origY + dy)) + 'px';
    }
    function mu() {
      document.removeEventListener('mousemove', mm);
      document.removeEventListener('mouseup', mu);
      if (dragging) {
        var sw2 = window.innerWidth;
        var cur = parseInt(fab.style.left) || 0;
        fab.style.left = (cur < sw2 / 2) ? '0px' : (sw2 - 60) + 'px';
        dragging = false;
      }
    }
    document.addEventListener('mousemove', mm);
    document.addEventListener('mouseup', mu);
  });
})();

function calibrate() {
  fetch('/calibrate', { method: 'POST' }).catch(() => {});
}

// ====== 连接检测 ======
let _connCheckRunning = true;
window.addEventListener('pagehide', () => { _connCheckRunning = false; });
window.addEventListener('beforeunload', () => { _connCheckRunning = false; });

(async function checkConn() {
  const dot = document.querySelector('.conn-dot');
  const txt = $('conn-text');
  let ok = false, failWait = 500;
  while (_connCheckRunning) {
    try {
      const ctrl = new AbortController();
      const tid = setTimeout(() => ctrl.abort(), 3000);
      const r = await fetch('/status', { signal: ctrl.signal });
      clearTimeout(tid);
      ok = r.ok;
    } catch { ok = false; }
    if (ok) {
      connected = true;
      failWait = 500;
      dot.className = 'conn-dot green';
      txt.className = 'conn-text on';
      txt.textContent = '已连接';
      await new Promise(r => setTimeout(r, 2000));
    } else {
      connected = false;
      if (window.tp) {
        tp.accDx = 0; tp.accDy = 0;
        tp.f2AccDx = 0; tp.f2AccDy = 0;
      }
      dot.className = 'conn-dot red';
      txt.className = 'conn-text';
      txt.textContent = '未连接';
      await new Promise(r => setTimeout(r, failWait));
      failWait = Math.min(failWait * 2, 5000);
    }
  }
})();

// ==================== 触控板 ====================

const CFG = {
  SENSITIVITY: 2.8,
  TAP_MS: 200,
  SCROLL_SENSITIVITY: 0.42,
};

const tp = {
  touching: false, lastX: 0, lastY: 0,
  accDx: 0, accDy: 0, touchStart: 0,
  twoFinger: false,
  f2CenterX: 0, f2CenterY: 0,
  f2AccDx: 0, f2AccDy: 0, f2Moved: false,
  movePending: false, scrollPending: false,
};

function flushTouch() {
  if (!connected) {
    tp.accDx = 0; tp.accDy = 0;
    tp.f2AccDx = 0; tp.f2AccDy = 0;
    tp.movePending = false; tp.scrollPending = false;
    return;
  }
  if (tp.movePending) {
    const dx = Math.round(tp.accDx);
    const dy = Math.round(tp.accDy);
    tp.accDx -= dx; tp.accDy -= dy;
    if (dx !== 0 || dy !== 0) api('/mouse-move', { dx, dy });
    tp.movePending = false;
  }
  if (tp.scrollPending) {
    const sx = Math.round(tp.f2AccDx * 10);
    const sy = Math.round(tp.f2AccDy * 10);
    tp.f2AccDx = 0; tp.f2AccDy = 0;
    if (sx !== 0 || sy !== 0) api('/scroll', { dx: sx, dy: sy });
    tp.scrollPending = false;
  }
}

function throttle() {
  flushTouch();
  if (tp.touching || tp.twoFinger) {
    requestAnimationFrame(throttle);
  }
}

function onSingleStart(x, y) {
  tp.twoFinger = false;
  tp.lastX = x; tp.lastY = y;
  tp.accDx = 0; tp.accDy = 0;
  tp.touchStart = performance.now();
  tp.touching = true;
  tp.movePending = false;
  requestAnimationFrame(throttle);
}
function onSingleMove(x, y) {
  if (!tp.touching) return;
  tp.accDx += (x - tp.lastX) * CFG.SENSITIVITY;
  tp.accDy += (y - tp.lastY) * CFG.SENSITIVITY;
  tp.lastX = x; tp.lastY = y;
  tp.movePending = true;
}
function onSingleEnd() {
  if (!tp.touching) return;
  tp.touching = false;
  tp.accDx = 0; tp.accDy = 0;
  tp.movePending = false;
  if (performance.now() - tp.touchStart < CFG.TAP_MS) {
    api('/mouse-click', { type: 'single' });
  }
}

function onTwoStart(x, y) {
  tp.twoFinger = true; tp.touching = false;
  tp.f2CenterX = x; tp.f2CenterY = y;
  tp.f2AccDx = 0; tp.f2AccDy = 0;
  tp.f2Moved = false; tp.touchStart = performance.now();
  tp.scrollPending = false;
  requestAnimationFrame(throttle);
}
function onTwoMove(cx, cy) {
  const ddx = cx - tp.f2CenterX;
  const ddy = cy - tp.f2CenterY;
  tp.f2CenterX = cx; tp.f2CenterY = cy;
  if (Math.abs(ddx) > 2 || Math.abs(ddy) > 2) tp.f2Moved = true;
  tp.f2AccDx += ddx * CFG.SCROLL_SENSITIVITY;
  tp.f2AccDy += ddy * CFG.SCROLL_SENSITIVITY;
  tp.scrollPending = true;
}
function onTwoEnd() {
  tp.twoFinger = false;
  tp.f2AccDx = 0; tp.f2AccDy = 0;
  tp.scrollPending = false;
  if (!tp.f2Moved) api('/rightclick');
}

if (pad) {
  pad.addEventListener('touchstart', e => {
    e.preventDefault();
    if (ta) ta.blur();
    const t = e.touches;
    if (t.length >= 2) {
      const cx = (t[0].clientX + t[1].clientX) / 2;
      const cy = (t[0].clientY + t[1].clientY) / 2;
      onTwoStart(cx, cy); return;
    }
    onSingleStart(t[0].clientX, t[0].clientY);
  });
  pad.addEventListener('touchmove', e => {
    e.preventDefault();
    const t = e.touches;
    if (t.length >= 2) {
      const cx = (t[0].clientX + t[1].clientX) / 2;
      const cy = (t[0].clientY + t[1].clientY) / 2;
      onTwoMove(cx, cy); return;
    }
    onSingleMove(t[0].clientX, t[0].clientY);
  });
  pad.addEventListener('touchend', e => {
    e.preventDefault();
    if (tp.twoFinger) { onTwoEnd(); return; }
    onSingleEnd();
  });
}

// ==================== 文字输入 ====================

if (ta) {
  ta.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendText();
    }
  });
}

window.sendText = async function sendText() {
  if (sending) return;
  const t = (ta ? ta.value : '').trim();
  if (!t) { if (st) st.textContent = '请输入内容'; return; }
  sending = true;
  if (st) st.textContent = '⏳ 发送中';
  try {
    const ctrl = new AbortController();
    const tid = setTimeout(() => ctrl.abort(), 5000);
    const r = await fetch('/send', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: t }),
      signal: ctrl.signal,
    });
    clearTimeout(tid);
    const d = await r.json();
    if (st) st.textContent = d.ok ? '✅ 已发送！' : '❌ 失败';
    if (d.ok && ta) ta.value = '';
  } catch {
    if (st) st.textContent = '❌ 发送超时，请重试';
  }
  sending = false;
};

window.sendPaste = async function sendPaste() {
  if (sending) return;
  const t = (ta ? ta.value : '').trim();
  if (!t) { if (st) st.textContent = '请输入内容'; return; }
  sending = true;
  if (st) st.textContent = '⏳ 粘贴中';
  try {
    const ctrl = new AbortController();
    const tid = setTimeout(() => ctrl.abort(), 5000);
    const r = await fetch('/send-typing', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: t }),
      signal: ctrl.signal,
    });
    clearTimeout(tid);
    const d = await r.json();
    if (st) st.textContent = d.ok ? '📋 已粘贴' : '❌ 失败';
    if (d.ok && ta) ta.value = '';
  } catch {
    if (st) st.textContent = '❌ 粘贴超时，请重试';
  }
  sending = false;
};

window.sendEnter = async function sendEnter() {
  if (st) st.textContent = '⏎';
  try { await fetch('/enter'); if (st) st.textContent = '✅ 回车'; } catch {}
  setTimeout(() => { if (st) st.textContent = ''; }, 1500);
};

// ==================== SendInput 模式切换 ====================

let sendinputEnabled = false;

async function checkSendInputStatus() {
  try {
    const r = await fetch('/sendinput-status');
    if (!r.ok) return;
    const s = await r.json();
    sendinputEnabled = s.enabled;
    const label = document.getElementById('mode-label');
    const btn = document.getElementById('mode-toggle-btn');
    if (label) {
      label.textContent = s.enabled ? '键鼠二' : '键鼠一';
      label.className = 'conn-text' + (s.enabled ? ' si-on' : '');
    }
    if (btn) btn.className = s.enabled ? 'sendinput' : 'legacy';
  } catch {}
}

window.toggleSendInput = async function toggleSendInput() {
  const endpoint = sendinputEnabled ? '/sendinput-off' : '/sendinput-on';
  try {
    const r = await fetch(endpoint, { method: 'POST' });
    if (r.ok) await checkSendInputStatus();
  } catch {}
};

setInterval(checkSendInputStatus, 5000);
setTimeout(checkSendInputStatus, 500);

window.sendBS = async function sendBS() {
  if (st) st.textContent = '←';
  try { await fetch('/backspace'); if (st) st.textContent = '← 退格'; } catch {}
  setTimeout(() => { if (st) st.textContent = ''; }, 800);
};

window.sendDel = async function sendDel() {
  if (st) st.textContent = '⌫';
  try { await fetch('/delete'); if (st) st.textContent = '✅ 已清空行'; } catch {}
  setTimeout(() => { if (st) st.textContent = ''; }, 2000);
};
