// SendInput 守护进程管理模块 — 独立模块，不干扰原有 mouse.js / keyboard.js
// 使用 input-daemon.exe (SendInput API)，绕过千问等应用的输入拦截
'use strict';

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const config = require('../config');

// ========== 状态 ==========

const _state = {
  proc: null,
  rl: null,
  enabled: false,         // 是否启用 SendInput 模式
  available: false,       // input-daemon.exe 是否存在
  _resolve: null,
  _lines: [],
};

// ========== 内部 ==========

/** 向守护进程发命令，返回行数组 */
function _query(cmd) {
  return new Promise(resolve => {
    if (!_state.proc || !_state.proc.stdin.writable) return resolve(null);
    if (_state._resolve) return resolve(null);
    _state._lines = [];
    _state._resolve = () => {
      const lines = _state._lines.slice();
      _state._lines = [];
      _state._resolve = null;
      resolve(lines);
    };
    _state.proc.stdin.write(cmd + '\n');
    setTimeout(() => {
      if (_state._resolve) {
        const r = _state._lines.slice();
        _state._lines = [];
        _state._resolve = null;
        resolve(r);
      }
    }, 2000);
  });
}

// ========== 公开 API ==========

/** 初始化：检查 exe 是否存在 */
function init() {
  const exe = path.join(config.BIN_DIR, 'input-daemon.exe');
  if (!fs.existsSync(exe)) {
    console.log('  [sendinput] input-daemon.exe 未找到，SendInput 模式不可用');
    _state.available = false;
    return false;
  }
  _state.available = true;
  console.log('  [sendinput] input-daemon.exe 已就绪');
  return true;
}

/** 启用 SendInput 模式 */
function enable() {
  if (!_state.available) return false;
  if (_state.enabled) return true;
  if (_state.proc) return true; // 已在运行

  const exe = path.join(config.BIN_DIR, 'input-daemon.exe');
  _state.proc = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });

  _state.rl = readline.createInterface({ input: _state.proc.stdout });
  _state.rl.on('line', line => {
    const t = line.trim();
    if (t === 'READY') {
      console.log('  [sendinput] 守护进程已就绪');
    } else {
      if (_state._resolve) {
        _state._lines.push(t);
        if (t === 'SCR_DONE' || t.startsWith('ERR')) {
          _state._resolve();
        }
      }
    }
  });

  _state.proc.stderr.on('data', d => {
    console.error('  [sendinput] error:', d.toString().trim());
  });

  _state.proc.on('exit', code => {
    console.log(`  [sendinput] 守护进程退出 (${code})`);
    _state.proc = null;
    _state.rl = null;
    _state.enabled = false;
  });

  _state.proc.on('error', err => {
    console.error('  [sendinput] 启动失败:', err.message);
    _state.proc = null;
    _state.enabled = false;
  });

  _state.enabled = true;
  return true;
}

/** 禁用 SendInput 模式 */
function disable() {
  _state.enabled = false;
  if (_state.proc) {
    try { _state.proc.kill(); } catch { /* ignore */ }
    _state.proc = null;
    _state.rl = null;
  }
}

// ========== 鼠标命令 ==========

/** 鼠标相对移动 */
function mouseMove(dx, dy) {
  if (!_state.enabled || !_state.proc) return false;
  try {
    _state.proc.stdin.write(`m ${dx} ${dy}\n`);
    return true;
  } catch { return false; }
}

/** 鼠标绝对移动 */
function mouseGoto(x, y) {
  if (!_state.enabled || !_state.proc) return false;
  try {
    _state.proc.stdin.write(`g ${x} ${y}\n`);
    return true;
  } catch { return false; }
}

/** 左键单击 */
function leftClick() {
  if (!_state.enabled || !_state.proc) return false;
  try { _state.proc.stdin.write('c\n'); return true; } catch { return false; }
}

/** 左键双击 */
function doubleClick() {
  if (!_state.enabled || !_state.proc) return false;
  try { _state.proc.stdin.write('d\n'); return true; } catch { return false; }
}

/** 右键单击 */
function rightClick() {
  if (!_state.enabled || !_state.proc) return false;
  try { _state.proc.stdin.write('r\n'); return true; } catch { return false; }
}

/** 滚动 */
function scroll(dx, dy) {
  if (!_state.enabled || !_state.proc) return false;
  try {
    if (dy) _state.proc.stdin.write(`s ${dy}\n`);
    if (dx) _state.proc.stdin.write(`h ${dx}\n`);
    return true;
  } catch { return false; }
}

/** 按下左键（拖拽开始） */
function dragStart() {
  if (!_state.enabled || !_state.proc) return false;
  try { _state.proc.stdin.write('t\n'); return true; } catch { return false; }
}

/** 释放左键（拖拽结束） */
function dragEnd() {
  if (!_state.enabled || !_state.proc) return false;
  try { _state.proc.stdin.write('u\n'); return true; } catch { return false; }
}

// ========== 键盘命令 ==========

/** 输入文本（支持中文） */
function typeText(text) {
  if (!_state.enabled || !_state.proc) return false;
  try {
    const safe = text.replace(/\n/g, ' ').replace(/\r/g, '');
    _state.proc.stdin.write(`k ${safe}\n`);
    return true;
  } catch { return false; }
}

/** 输入文本 + 回车 */
function typeTextEnter(text) {
  if (!_state.enabled || !_state.proc) return false;
  try {
    const safe = text.replace(/\n/g, ' ').replace(/\r/g, '');
    _state.proc.stdin.write(`k ${safe}\n`);
    _state.proc.stdin.write('e\n');
    return true;
  } catch { return false; }
}

/** 按键（通过名称，如 enter, tab, esc 等） */
function pressKey(keyName) {
  if (!_state.enabled || !_state.proc) return false;
  try {
    _state.proc.stdin.write(`p ${keyName}\n`);
    return true;
  } catch { return false; }
}

/** 退格 */
function backspace() {
  if (!_state.enabled || !_state.proc) return false;
  try { _state.proc.stdin.write('b\n'); return true; } catch { return false; }
}

/** 回车 */
function enter() {
  if (!_state.enabled || !_state.proc) return false;
  try { _state.proc.stdin.write('e\n'); return true; } catch { return false; }
}

/** 检测屏幕 */
async function detectScreens() {
  const lines = await _query('screens');
  if (!lines) return null;
  return lines.filter(l => l.startsWith('SCR|')).map(l => {
    const p = l.split('|');
    return { x: +p[1], y: +p[2], w: +p[3], h: +p[4], primary: p[5] === '1', name: p[6] || '' };
  });
}

/** 获取当前状态 */
function getStatus() {
  return {
    available: _state.available,
    enabled: _state.enabled,
    running: _state.proc !== null,
  };
}

// ========== 导出 ==========

module.exports = {
  init,
  enable,
  disable,
  mouseMove,
  mouseGoto,
  leftClick,
  doubleClick,
  rightClick,
  scroll,
  dragStart,
  dragEnd,
  typeText,
  typeTextEnter,
  pressKey,
  backspace,
  enter,
  detectScreens,
  getStatus,
};
