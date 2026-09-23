#!/usr/bin/env node
// ⌨️ 键盘注入模块 — 通过 PowerShell SendKeys + Clipboard 模拟键盘输入
'use strict';

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const config = require('../config');

// ========== 常量 ==========
const CLIP_TEMP = path.join(config.DATA_DIR, '.clip_temp.txt');
const KEY_MAP = config.KEY_MAP;

// ========== 内部 ==========

/** 执行 PowerShell 命令（Base64 编码以避免转义地狱） */
function _runPS(code) {
  const buf = Buffer.from(code, 'utf16le').toString('base64');
  const p = spawn('powershell', [
    '-NoProfile', '-NonInteractive', '-EncodedCommand', buf,
  ], { stdio: 'ignore', windowsHide: true });
  p.on('error', () => { /* 静默 */ });
}

/**
 * 核心：设置剪贴板 → Ctrl+V → 可选回车
 * 通过临时文件传递中文，避免 PowerShell here-string 编码问题
 */
function _clipPaste(text, pressEnter) {
  const t = text.replace(/[\r\n]+/g, ' ');
  if (!t) return;

  // 写 UTF-8 临时文件供 PowerShell 读取（规避 here-string 编码问题）
  fs.writeFileSync(CLIP_TEMP, t, 'utf-8');

  const extraEnter = pressEnter
    ? `[System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); Start-Sleep -Milliseconds 30;`
    : '';

  _runPS(`
$t = Get-Content "${CLIP_TEMP}" -Raw -Encoding UTF8;
Add-Type -AssemblyName System.Windows.Forms;
[System.Windows.Forms.Clipboard]::SetText($t);
Start-Sleep -Milliseconds ${config.CLIP_SETTLE};
[System.Windows.Forms.SendKeys]::SendWait("^{v}");
Start-Sleep -Milliseconds ${pressEnter ? config.SEND_HOLD_ENTER : config.SEND_HOLD_NOENTER};
${extraEnter}
[System.Windows.Forms.Clipboard]::Clear()
`);
}

// ========== 公开 API ==========

/** 发送文字（粘贴 + 回车） */
function sendText(text) {
  _clipPaste(text, true);
}

/** 发送文字（仅粘贴，不回车） */
function sendTextNoEnter(text) {
  _clipPaste(text, false);
}

/** 发送按键序列（如 {UP}{ENTER}） */
function sendKeys(keys) {
  if (!keys) return;
  fs.writeFileSync(CLIP_TEMP, keys, 'utf-8');
  _runPS(`
Add-Type -AssemblyName System.Windows.Forms;
Start-Sleep -Milliseconds ${config.CLIP_SETTLE};
[System.Windows.Forms.SendKeys]::SendWait((Get-Content "${CLIP_TEMP}" -Raw -Encoding UTF8));
`);
}

/** 发送退格 */
function sendBackspace() {
  _runPS(`
Add-Type -AssemblyName System.Windows.Forms;
[System.Windows.Forms.SendKeys]::SendWait("{BACKSPACE}");
Start-Sleep -Milliseconds 150;
`);
}

/** 删除整行（Ctrl+E Ctrl+U — 行首到行尾） */
function sendDeleteLine() {
  _runPS(`
Add-Type -AssemblyName System.Windows.Forms;
[System.Windows.Forms.SendKeys]::SendWait("^{e}^{u}");
`);
}

/** 发送回车（用剪贴板守护进程方式） */
function sendEnter() {
  _runPS(`
Add-Type -AssemblyName System.Windows.Forms;
[System.Windows.Forms.Clipboard]::SetText(" ");
Start-Sleep -Milliseconds ${config.CLIP_SETTLE};
[System.Windows.Forms.SendKeys]::SendWait("^{v}");
Start-Sleep -Milliseconds 80;
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}");
`);
}

/** 通过名称/action 查找按键映射并发送 */
function sendAction(action) {
  if (!action) return false;
  const a = action.toLowerCase().trim();
  if (/^[0-9]$/.test(a)) {
    sendKeys(a);
    return true;
  }
  const k = KEY_MAP[a];
  if (k) {
    sendKeys(k);
    return true;
  }
  return false;
}

// ========== 导出 ==========

module.exports = {
  sendText,
  sendTextNoEnter,
  sendKeys,
  sendBackspace,
  sendDeleteLine,
  sendEnter,
  sendAction,
};
