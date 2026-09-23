#!/usr/bin/env node
// 📱 Inkm Desktop — 桌面管理器（通道监控 + 系统托盘）
'use strict';

const { app, BrowserWindow, ipcMain, clipboard, Tray, Menu, nativeImage } = require('electron');
const path = require('path');
const { spawn, exec } = require('child_process');
const fs = require('fs');
const ChannelManager = require('../server/src/channel-manager');
const TunnelManager = require('../server/src/tunnel-manager');
const utils = require('../server/src/utils');
const config = require('../server/src/config');
const { createLogger } = require('../server/src/logger');

const logger = createLogger('desktop');

// ========== 常量 ==========
const PORT_CHECK_TIMEOUT = 5000;
const PORT_CHECK_INTERVAL = 100;
const NODE_PATH_FILE = path.join(config.DATA_DIR, '.node_path');

let win;
let tray;
let _startingServer = false; // 防止并发 startServer()
let _menuTimer;               // updateMenu 定时器句柄

// ========== 管理器 ==========
const channelMgr = new ChannelManager({
  checkInterval: 15000,
  maxReconnectAttempts: 999,
});
const tunnelMgr = new TunnelManager();

// ========== 工具 ==========

let _cachedNodePath = null;

/** 查找 node 可执行文件路径：优先本地便携版，其次缓存，最后系统路径 */
async function findNodePath() {
  if (_cachedNodePath) return _cachedNodePath;

  // 1. 优先使用本地便携版 node.exe（项目内置）
  const LOCAL_NODE = path.join(config.BIN_DIR, 'node.exe');
  try {
    if (fs.existsSync(LOCAL_NODE)) {
      logger.info(`使用内置 node: ${LOCAL_NODE}`);
      return (_cachedNodePath = LOCAL_NODE);
    }
  } catch { /* 忽略 */ }

  // 2. 尝试读取缓存的 node 路径
  try {
    if (fs.existsSync(NODE_PATH_FILE)) {
      const cached = fs.readFileSync(NODE_PATH_FILE, 'utf-8').trim();
      if (cached && fs.existsSync(cached)) {
        logger.info(`使用缓存的 node 路径: ${cached}`);
        return (_cachedNodePath = cached);
      }
    }
  } catch { /* 忽略 */ }

  // 3. where node
  try {
    const nodePath = await new Promise(r =>
      exec('where node', { timeout: 2000 }, (e, o) => r(e || !o ? null : o.trim().split('\n')[0]))
    );
    if (nodePath) {
      logger.info(`where node 找到: ${nodePath}`);
      return (_cachedNodePath = nodePath);
    }
  } catch { /* 忽略 */ }

  // 4. 扫描常见 Node.js 安装路径
  const candidates = [];
  const pf = process.env.ProgramFiles || 'C:\\Program Files';
  const pf86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)';
  const localAppData = process.env.LOCALAPPDATA || '';
  const userProfile = process.env.USERPROFILE || '';
  candidates.push(path.join(pf, 'nodejs', 'node.exe'));
  candidates.push(path.join(pf86, 'nodejs', 'node.exe'));
  candidates.push(path.join(localAppData, 'fnm', 'current', 'node.exe'));
  candidates.push(path.join(localAppData, 'nvs', 'default', 'node.exe'));
  // nvm-windows: 扫描 nvm 目录下的所有版本
  const nvmDir = path.join(userProfile, 'AppData', 'Roaming', 'nvm');
  if (fs.existsSync(nvmDir)) {
    try {
      const dirs = fs.readdirSync(nvmDir).filter(d => /^v?\d+\.\d+\.\d+/.test(d));
      dirs.sort().reverse(); // 取最新版本
      if (dirs.length) candidates.push(path.join(nvmDir, dirs[0], 'node.exe'));
    } catch { /* 忽略 */ }
  }
  for (const c of candidates) {
    try {
      if (fs.existsSync(c)) {
        logger.info(`扫描路径找到 node: ${c}`);
        return (_cachedNodePath = c);
      }
    } catch { /* 忽略 */ }
  }

  logger.warn('所有方式均未找到 node，将使用 "node" 默认值');
  return (_cachedNodePath = 'node');
}

function waitForPort(timeoutMs = PORT_CHECK_TIMEOUT) {
  return new Promise(resolve => {
    const deadline = Date.now() + timeoutMs;
    const tryConnect = () => {
      utils.checkPort().then(ok => {
        if (ok) return resolve(true);
        if (Date.now() >= deadline) return resolve(false);
        setTimeout(tryConnect, PORT_CHECK_INTERVAL);
      });
    };
    tryConnect();
  });
}

/** 等待端口释放（与 waitForPort 相反） */
function waitForPortFree(timeoutMs = 2000) {
  return new Promise(resolve => {
    const deadline = Date.now() + timeoutMs;
    const tryFree = () => {
      utils.checkPort().then(ok => {
        if (!ok) return resolve(true);  // 端口已释放
        if (Date.now() >= deadline) return resolve(false);
        setTimeout(tryFree, 100);  // 100ms 快速轮询
      });
    };
    tryFree();
  });
}

// ========== 服务器管理 ==========

async function startServer() {
  if (_startingServer) return { ok: false, status: 'starting' };
  _startingServer = true;
  try {
    const alreadyRunning = await utils.checkPort();
    if (alreadyRunning) {
      channelMgr.setChannelStatus('server', 'online');
      channelMgr.setChannelStatus('lan', 'online');
      _sendStatus();
      _updateTrayTooltip('true');
      return { ok: true, status: 'already' };
    }

    // 启动 server.js（最多重试 2 次）
    let running = false;
    for (let attempt = 1; attempt <= 2; attempt++) {
      if (attempt > 1) {
        logger.info(`启动重试 (第 ${attempt} 次)...`);
        await utils.sleep(1500);
      }

      const nodePath = await findNodePath();
      logger.info(`node 路径: ${nodePath}`);
      const proc = spawn(nodePath, ['index.js'], {
        cwd: config.PHONE_DIR, windowsHide: true, stdio: 'ignore', detached: true,
      });
      proc.on('error', err => logger.error(`启动失败: ${err.message}`));
      proc.unref();

      running = await waitForPort();
      if (running) break;
    }

    if (running) {
      channelMgr.setChannelStatus('server', 'online');
      channelMgr.setChannelStatus('lan', 'online');
      channelMgr.setEnabled('server', true);  // 启用 crash 自动恢复
      checkFirewall(); // 检查防火墙规则
    } else {
      logger.warn('启动失败');
    }
    _sendStatus();
    _updateTrayTooltip(running ? 'true' : 'false');

    return { ok: running, status: running ? 'started' : 'failed' };
  } finally {
    _startingServer = false;
  }
}

async function stopServer() {
  await tunnelMgr.disconnect();
  // 杀掉占用端口的进程
  const pid = await utils.findPidByPort(config.PORT);
  if (pid) { await utils.killPid(pid); }
  // 快速等端口释放（taskkill 后端口几乎立刻释放）
  await waitForPortFree(1500);

  channelMgr.stop();
  channelMgr.setEnabled('server', false);

  // 检查服务是否真的停了，防止状态不同步
  const actuallyRunning = await utils.checkPort();
  if (actuallyRunning) {
    channelMgr.setChannelStatus('server', 'online');
    channelMgr.setChannelStatus('lan', 'online');
  }

  _sendStatus();
  _updateTrayTooltip(actuallyRunning ? 'true' : 'false');
  return { ok: !actuallyRunning, status: actuallyRunning ? 'failed' : 'stopped' };
}

// ========== 防火墙检测 ==========

let _firewallOk = true;
let _fwCheckTimer = null;

async function checkFirewall() {
  const ok = await utils.checkFirewallRule('Inkm 智能键鼠');
  if (ok !== _firewallOk) {
    _firewallOk = ok;
    if (!ok) logger.warn('防火墙规则缺失，手机可能无法通过局域网连接');
    pushStatus(); // 推送变化
  }
  return ok;
}

function startFirewallCheck() {
  if (_fwCheckTimer) return;
  checkFirewall();
  _fwCheckTimer = setInterval(checkFirewall, 60000); // 每 60s 复查
}

// ========== 推送到渲染进程 ==========

function _sendStatus() {
  if (!win || win.isDestroyed()) return;
  const ch = channelMgr.getStatus();
  const t = tunnelMgr.getStatus();
  win.webContents.send('channel-status', {
    ...ch,
    cloudflare: {
      name: 'cloudflare', label: 'Cloudflare',
      status: t.status, url: t.url || '未连接',
      lastError: t.error, failCount: 0, enabled: false, lastCheck: 0,
    },
    firewallOk: _firewallOk,
  });
}

let _pushPending = false;
function pushStatus() {
  if (_pushPending || !win || win.isDestroyed()) return;
  _pushPending = true;
  setImmediate(() => {
    _pushPending = false;
    _sendStatus();
  });
}

// ========== 托盘 ==========

/** 加载 desktop/mouse.ico 环状图标 */
let _trayIcon = null;
function _makeTrayIcon() {
  if (_trayIcon) return _trayIcon;
  const ico = path.join(__dirname, 'mouse.ico');
  try {
    if (fs.existsSync(ico)) {
      _trayIcon = nativeImage.createFromPath(ico);
      return _trayIcon;
    }
  } catch {}
  // 回退：绿点
  const w = 16, h = 16;
  const buf = Buffer.alloc(w * h * 4);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const cx = x - w/2 + 0.5, cy = y - h/2 + 0.5;
      const i = (y * w + x) * 4;
      const inside = cx*cx + cy*cy < 56;
      buf[i] = inside ? 16 : 0;
      buf[i+1] = inside ? 185 : 0;
      buf[i+2] = inside ? 129 : 0;
      buf[i+3] = inside ? 255 : 0;
    }
  }
  _trayIcon = nativeImage.createFromBuffer(buf, { width: w, height: h });
  return _trayIcon;
}

function _updateTrayTooltip(state) {
  if (!tray) return;
  const tips = {
    true: '智能键鼠 — 服务运行中',
    false: '智能键鼠 — 服务未运行',
    starting: '智能键鼠 — 启动中...',
    stopping: '智能键鼠 — 停止中...',
  };
  tray.setToolTip(tips[state] || tips.false);
}

function createTray() {
  if (tray) { tray.destroy(); tray = null; }
  tray = new Tray(_makeTrayIcon());
  tray.setToolTip('智能键鼠');

  const updateMenu = () => {
    // 直接检测端口，不依赖 channelMgr 状态同步（channelMgr 可能已停）
    utils.checkPort(config.PORT, '127.0.0.1', 500).then(portRunning => {
      if (!tray) return;
      const serverOnline = portRunning;
      const tunnelStatus = tunnelMgr.getStatus();
      const tunnelUrl = tunnelStatus.status === 'online' && tunnelStatus.url ? tunnelStatus.url : '';

      tray.setContextMenu(Menu.buildFromTemplate([
        { label: serverOnline ? '✅ 服务运行中' : '⏸ 服务未运行', enabled: false },
        { label: tunnelUrl ? `☁️ ${tunnelUrl}` : '☁️ 隧道未连接', enabled: false },
        { type: 'separator' },
        { type: 'separator' },
        {
          label: '退出',
          click: async () => {
            await stopServer();
            if (tray) {
              tray.removeAllListeners('click');
              tray.destroy();
              tray = null;
            }
            app.quit();
          },
        },
      ]));
    }).catch(() => {});
  };

  tray.on('click', () => {
    if (tray && win) { win.show(); win.focus(); }
  });

  // 每 2 秒刷新托盘菜单状态
  updateMenu();
  _menuTimer = setInterval(() => {
    if (tray) updateMenu();
  }, 2000);
}

// ========== IPC ==========

ipcMain.handle('server-start', startServer);
ipcMain.handle('server-stop', stopServer);

ipcMain.handle('server-status', async () => {
  const running = await utils.checkPort();
  return { running, urls: running ? utils.getChannelUrls() : null };
});

ipcMain.handle('clipboard-write', (_, text) => {
  clipboard.writeText(text);
  return true;
});

ipcMain.handle('channel-status', () => {
  const ch = channelMgr.getStatus();
  const t = tunnelMgr.getStatus();
  return {
    ...ch,
    cloudflare: { name:'cloudflare', label:'Cloudflare', status:t.status, url:t.url||'未连接', lastError:t.error, failCount:0, enabled:false, lastCheck:0 },
  };
});

ipcMain.handle('channel-connect', async () => {
  return tunnelMgr.connect();
});

ipcMain.handle('channel-disconnect', async () => {
  return tunnelMgr.disconnect();
});

// ========== 窗口 ==========

function createWindow(showOnReady = true) {
  const icoPath = path.join(__dirname, 'mouse.ico');
  win = new BrowserWindow({
    width: 500,
    height: 640,
    frame: false,
    show: false,
    resizable: true,
    minWidth: 420,
    minHeight: 520,
    backgroundColor: '#f2f2f2',
    icon: icoPath,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
    },
  });

  win.once('ready-to-show', () => {
    if (showOnReady) {
      win.show();
    }
  });

  // 关闭时隐藏到托盘而非退出
  win.on('close', e => {
    if (!app.isQuitting) {
      e.preventDefault();
      win.hide();
    }
  });

  win.loadFile('renderer/index.html');

  pushStatus();
  setInterval(pushStatus, 2000);
}

// ========== 应用生命周期 ==========

app.isQuitting = false;

// 防止多开 — 确保只有一个实例、一个托盘
const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (win) { win.show(); win.focus(); }
  });

  app.whenReady().then(async () => {
    // 预先解析 node 路径传给 channelMgr
    const nodePath = await findNodePath();
    channelMgr.setNodePath(nodePath);
    logger.info(`node 路径: ${nodePath}`);

    createTray();
    createWindow(true); // 显示窗口

    // 先检测服务端是否已在运行
    const running = await utils.checkPort();
    if (running) {
      channelMgr.setChannelStatus('server', 'online');
      channelMgr.setChannelStatus('lan', 'online');
    }

    // 启动通道监控，但禁用自动重连（防止与用户操作竞争）
    channelMgr.setEnabled('server', false);
    channelMgr.start();
    _sendStatus();
    _updateTrayTooltip(running ? 'true' : 'false');

    startFirewallCheck(); // 开始定期检测防火墙

    // 不自动启动 — 让用户通过界面按钮控制
  });
}

app.on('before-quit', () => {
  app.isQuitting = true;
  // 清理后台定时器和检测
  channelMgr.stop();
  if (_menuTimer) { clearInterval(_menuTimer); _menuTimer = null; }
  if (_fwCheckTimer) { clearInterval(_fwCheckTimer); _fwCheckTimer = null; }
});

app.on('window-all-closed', () => {
  // 不退出，保留托盘
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
  if (win) { win.show(); win.focus(); }
});

// 窗口控制
ipcMain.on('win-minimize', () => win?.minimize());
ipcMain.on('win-maximize', () => win?.isMaximized() ? win.unmaximize() : win.maximize());
ipcMain.on('win-close', () => win?.hide());
