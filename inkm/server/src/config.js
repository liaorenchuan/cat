#!/usr/bin/env node
// ⚙️ 集中配置 — 所有模块从这里读取常量
'use strict';

const path = require('path');
const os = require('os');
const fs = require('fs');

// SEA 模式检测：独立 exe 时 process.argv[1] 等于 exe 自身路径而非脚本
const IS_SEA = !process.argv[1] || process.argv[1] === process.execPath;

// 便携基座：SEA 模式取 exe 所在目录，开发模式保持原样
const BASE_DIR = IS_SEA
  ? path.dirname(process.execPath)
  : path.resolve(__dirname, '..', '..');  // server/../.. = 项目根

const PHONE_DIR = IS_SEA
  ? path.join(BASE_DIR, 'server')
  : path.resolve(__dirname, '..');
const DATA_DIR = IS_SEA
  ? path.join(BASE_DIR, 'data')
  : path.resolve(__dirname, '..', '..', 'data');
const BIN_DIR = IS_SEA
  ? path.join(BASE_DIR, 'server', 'bin')
  : path.resolve(__dirname, '..', 'bin');

const config = {

  // ========== 服务 ==========
  PORT: 3456,
  PHONE_DIR,
  DATA_DIR,

  // ========== 二进制文件目录 ==========
  BIN_DIR,

  // ========== 网络检测 ==========
  CHECK_TIMEOUT: 5000,
  DEFAULT_USER_AGENT: 'Inkm/1.0',

  // ========== HTTP ==========
  BODY_LIMIT: 65536,

  // ========== 键盘/剪贴板 ==========
  CLIP_SETTLE: 50,
  SEND_HOLD_ENTER: 120,
  SEND_HOLD_NOENTER: 30,

  // ========== SendInput 模式（输入注入增强，绕过千问等拦截） ==========
  USE_SENDINPUT: false,       // false = 使用原始 mouse-daemon + PowerShell SendKeys

  // ========== 鼠标常驻进程 ==========
  MOUSE_RESTART_DELAY: 500,

  // ========== 通道管理 ==========
  CHANNEL_CHECK_INTERVAL: 15000,
  RECONNECT_COOLDOWN: 8000,
  BACKOFF_BASE: 5000,
  MAX_RECONNECT_ATTEMPTS: 5,
  MAX_RECONNECT_MS: 60000,

  // ========== Cloudflare 隧道 ==========
  TUNNEL_PARSE_TIMEOUT: 15000,
  CLOUDFLARE_PATTERN: /https:\/\/[a-z0-9-]+\.trycloudflare\.com/,
  CF_BIN: path.join(BIN_DIR, 'cloudflared.exe'),

  // ========== 触控板灵敏度（前端也会用） ==========
  SENSITIVITY: 2.8,
  TAP_MS: 200,
  THROTTLE_MS: 16,
  SCROLL_SENSITIVITY: 2,

  // ========== MIME 类型映射 ==========
  MIME: new Map([
    ['.html', 'text/html; charset=utf-8'],
    ['.css',  'text/css; charset=utf-8'],
    ['.js',   'application/javascript; charset=utf-8'],
  ]),

  // ========== 按键映射 ==========
  KEY_MAP: {
    'up':        '{UP}',   'down':      '{DOWN}',
    'left':      '{LEFT}', 'right':     '{RIGHT}',
    'tab':       '{TAB}',  'tab-back':  '+{TAB}',
    'enter':     '{ENTER}', 'escape':   '{ESC}',
    'backspace': '{BACKSPACE}', 'delete': '{DELETE}',
    'allow':     '{ENTER}',
    'allow-once':'{DOWN}{ENTER}',
    'always':    '{DOWN}{DOWN}{ENTER}',
    'deny':      '{DOWN}{DOWN}{DOWN}{ENTER}',
  },
};

// 确保数据目录存在
try { fs.mkdirSync(DATA_DIR, { recursive: true }); } catch {}

module.exports = config;
