#!/usr/bin/env node
// 🖱️ 鼠标管理模块 — 常驻进程生命周期 + 命令交互
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
  resolve: null,
  lines: [],
  screenInfo: null,
  _restartTimer: null,
};

// ========== 内部 ==========

/** 向常驻进程发问询命令，返回行数组 */
function _query(cmd) {
  return new Promise(resolve => {
    if (!_state.proc || !_state.proc.stdin.writable) return resolve(null);
    if (_state.resolve) return resolve(null); // 已有问询进行中，跳过避免竞态
    _state.lines = [];
    _state.resolve = () => {
      const lines = _state.lines.slice();
      _state.lines = [];
      _state.resolve = null;
      resolve(lines);
    };
    _state.proc.stdin.write(cmd + '\n');
    setTimeout(() => {
      if (_state.resolve) {
        const r = _state.lines.slice();
        _state.lines = [];
        _state.resolve = null;
        resolve(r);
      }
    }, 3000);
  });
}

/** 解析屏幕信息 */
async function _detectScreens() {
  const lines = await _query('screens');
  if (!lines) {
    _state.screenInfo = null;
    return;
  }
  _state.screenInfo = lines.filter(l => l.startsWith('SCR|')).map(l => {
    const p = l.split('|');
    return { x: +p[1], y: +p[2], w: +p[3], h: +p[4], primary: p[5] === '1', name: p[6] || '' };
  });
  const totalW = Math.max(..._state.screenInfo.map(s => s.x + s.w), 0);
  const totalH = Math.max(..._state.screenInfo.map(s => s.y + s.h), 0);
  console.log(`  🖥️  检测到 ${_state.screenInfo.length} 个显示器 (${totalW}x${totalH})`);
}

// ========== 公开 API ==========

/** 启动鼠标常驻进程 */
function start() {
  if (_state.proc) return; // 已运行
  _state._stopped = false; // 重置停止标志
  const exe = path.join(config.PHONE_DIR, 'bin', 'mouse-daemon.exe');
  if (!fs.existsSync(exe)) {
    console.warn('  ⚠️  mouse-daemon.exe 未找到，鼠标功能不可用');
    return;
  }

  _state.proc = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });

  _state.rl = readline.createInterface({ input: _state.proc.stdout });
  _state.rl.on('line', line => {
    const t = line.trim();
    if (t === 'SCR_DONE' && _state.resolve) {
      _state.lines.push(t);
      _state.resolve();
    } else if (_state.resolve) {
      _state.lines.push(t);
    }
  });

  _state.proc.stderr.on('data', d => console.error('  [mouse-daemon]', d.toString().trim()));
  _state.proc.on('exit', code => {
    if (_state._stopped) return; // 主动 stop，不重启
    console.error(`  [mouse-daemon] 退出 (${code}), ${config.MOUSE_RESTART_DELAY}ms 后重启...`);
    _state.proc = null;
    _state.rl = null;
    _state._restartTimer = setTimeout(start, config.MOUSE_RESTART_DELAY);
  });
  _state.proc.on('error', err => {
    console.error('  [mouse-daemon] 启动失败:', err.message);
    _state.proc = null;
  });
}

/** 停止鼠标常驻进程 */
function stop() {
  _state._stopped = true;
  if (_state._restartTimer) {
    clearTimeout(_state._restartTimer);
    _state._restartTimer = null;
  }
  if (_state.proc) {
    try { _state.proc.kill(); } catch { /* 忽略 */ }
    _state.proc = null;
    _state.rl = null;
  }
  _state.screenInfo = null;
}

/** 发送命令（移动、点击、滚动等） */
function send(cmd) {
  if (_state.proc && _state.proc.stdin.writable) {
    _state.proc.stdin.write(cmd + '\n');
  }
}

/** 检测屏幕（延迟调用，让进程先启动完） */
function detectScreens(delayMs) {
  if (delayMs) {
    setTimeout(() => _detectScreens(), delayMs);
  } else {
    _detectScreens();
  }
}

/** 获取屏幕信息快照 */
function getScreenInfo() {
  return _state.screenInfo ? [..._state.screenInfo] : null;
}

/** 获取主屏幕 */
function getPrimaryScreen() {
  if (!_state.screenInfo || _state.screenInfo.length === 0) return null;
  return _state.screenInfo.find(s => s.primary) || _state.screenInfo[0];
}

// ========== 导出 ==========

module.exports = {
  start,
  stop,
  send,
  detectScreens,
  getScreenInfo,
  getPrimaryScreen,
};
