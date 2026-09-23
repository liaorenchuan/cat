#!/usr/bin/env node
// 📡 Channel Manager — 本地服务 + 局域网通道检测
'use strict';

const { spawn } = require('child_process');
const { EventEmitter } = require('events');
const config = require('./config');
const { createLogger } = require('./logger');
const utils = require('./utils');

const logger = createLogger('channel');

// ========== 通道定义 ==========

const CHANNEL_DEFS = {
  server: {
    label: '本地服务',
    url: () => `http://localhost:${config.PORT}`,
    enabled: true,
    check: (mgr) => mgr._checkServer(),
    reconnect: (mgr) => mgr._reconnectServer(),
  },
  lan: {
    label: '局域网',
    url: () => `http://${utils.getLanIP()}:${config.PORT}`,
    enabled: true,
  },
};

class ChannelManager extends EventEmitter {
  constructor(options = {}) {
    super();
    this._checkInterval = options.checkInterval || config.CHANNEL_CHECK_INTERVAL;
    this._maxAttempts = options.maxReconnectAttempts || config.MAX_RECONNECT_ATTEMPTS;
    this._nodePath = options.nodePath || null;
    this._stopRequested = false;
    this._checkTimer = null;
    this._reconnectTimers = {};

    this._channels = {};
    for (const [name, def] of Object.entries(CHANNEL_DEFS)) {
      this._channels[name] = {
        name, label: def.label, status: 'unknown',
        url: def.url(), failCount: 0,
        lastCheck: 0, lastError: '', enabled: def.enabled !== false,
      };
    }
  }

  // ==================== 公开 API ====================

  start() {
    if (this._checkTimer) return Promise.resolve();
    this._stopRequested = false;
    this.emit('start');
    logger.info(`已启动 (检测周期 ${this._checkInterval}ms)`);
    const firstCheck = this._checkAll();
    this._checkTimer = setInterval(() => this._checkAll(), this._checkInterval);
    return firstCheck;
  }

  stop() {
    this._stopRequested = true;
    if (this._checkTimer) {
      clearInterval(this._checkTimer);
      this._checkTimer = null;
    }
    for (const t of Object.keys(this._reconnectTimers)) {
      clearTimeout(this._reconnectTimers[t]);
      delete this._reconnectTimers[t];
    }
    for (const name of Object.keys(this._channels)) {
      this._updateStatus(name, 'offline', '已停止');
    }
    this.emit('stop');
    logger.info('已停止');
  }

  getStatus() {
    const snapshot = {};
    for (const [k, ch] of Object.entries(this._channels)) {
      snapshot[k] = { ...ch, url: (CHANNEL_DEFS[k]?.url() || ch.url) };
    }
    return snapshot;
  }

  async reconnect(name) {
    const ch = this._channels[name];
    if (!ch) { this.emit('error', `未知通道: ${name}`); return false; }
    if (ch.status === 'connecting') return false;
    ch.failCount = 0;
    return this._doReconnect(name);
  }

  setEnabled(name, enabled) {
    const ch = this._channels[name];
    if (!ch) return;
    ch.enabled = enabled;
    this.emit('channel-config', name, { enabled });
  }

  /** 直接设置某通道状态（用于外部模块启动后立即同步） */
  setChannelStatus(name, status, error = '') {
    this._updateStatus(name, status, error);
  }

  /** 设置 node 可执行文件路径（给 Electron 场景用） */
  setNodePath(path) {
    this._nodePath = path;
  }

  // ==================== 检测逻辑 ====================

  async _checkAll() {
    if (this._stopRequested) return;
    await this._checkServer();
    if (this._channels.server.status === 'online') {
      this._updateStatus('lan', 'online');
    }
    this.emit('status-update', this.getStatus());
  }

  async _checkServer() {
    const running = await utils.checkPort();
    if (running) {
      this._updateStatus('server', 'online');
      return true;
    }
    this._updateStatus('server', 'offline', `端口 ${config.PORT} 未监听`);
    if (this._channels.server.enabled) this._scheduleReconnect('server');
    return false;
  }

  // ==================== 内部 ====================

  _updateStatus(name, status, error = '') {
    const ch = this._channels[name];
    if (!ch) return;
    const prev = ch.status;
    ch.status = status;
    ch.lastCheck = Date.now();
    if (error) ch.lastError = error;
    if (status === 'online') {
      ch.failCount = 0;
    } else if (status === 'offline' && prev !== 'offline') {
      ch.failCount++;
    }
    if (prev !== status) {
      this.emit('channel-change', name, status, prev, error);
    }
  }

  _scheduleReconnect(name) {
    const ch = this._channels[name];
    if (!ch || ch.status === 'connecting') return;
    if (this._reconnectTimers[name]) return;
    if (ch.failCount >= this._maxAttempts) {
      this.emit('reconnect-giveup', name, `连续失败 ${ch.failCount} 次，放弃自动重连`);
      return;
    }

    const delay = Math.min(
      config.RECONNECT_COOLDOWN + ch.failCount * config.BACKOFF_BASE,
      config.MAX_RECONNECT_MS
    );
    this._reconnectTimers[name] = setTimeout(async () => {
      delete this._reconnectTimers[name];
      if (this._stopRequested) return;
      await this._doReconnect(name);
    }, delay);

    this.emit('reconnect-scheduled', name, delay, ch.failCount);
  }

  async _doReconnect(name) {
    const def = CHANNEL_DEFS[name];
    const ch = this._channels[name];
    if (!def || !ch) return false;

    this._updateStatus(name, 'connecting', '重连中...');
    this.emit('reconnect-start', name);
    logger.info(`[${ch.label}] 尝试重连 (第 ${ch.failCount + 1} 次)...`);

    const ok = def.reconnect ? await def.reconnect(this) : false;

    if (ok) {
      this._updateStatus(name, 'online');
      this.emit('reconnect-success', name);
      logger.info(`[${ch.label}] 重连成功`);
    } else {
      this._updateStatus(name, 'offline', '重连失败');
      this.emit('reconnect-fail', name);
      logger.info(`[${ch.label}] 重连失败`);
      if (ch.enabled && ch.failCount < this._maxAttempts) {
        this._scheduleReconnect(name);
      }
    }
    return ok;
  }

  async _reconnectServer() {
    try {
      const nodeExe = this._nodePath || (
        process.execPath.toLowerCase().endsWith('node.exe')
          ? process.execPath : 'node');
      const proc = spawn(nodeExe, ['index.js'], {
        cwd: config.PHONE_DIR, windowsHide: true, stdio: 'ignore', detached: true,
      });
      proc.unref();

      for (let i = 0; i < 20; i++) {
        await utils.sleep(500);
        if (this._stopRequested) return false;
        if (await utils.checkPort()) return true;
      }
      return false;
    } catch (err) {
      logger.error(`[server] 重连异常: ${err.message}`);
      return false;
    }
  }
}

// ==================== 独立运行 ====================

if (require.main === module) {
  const mgr = new ChannelManager({ checkInterval: 15000 });

  mgr.on('channel-change', (name, status, prev, error) => {
    const ch = mgr._channels[name];
    const icon = status === 'online' ? '✅' : status === 'connecting' ? '🔄' : '❌';
    console.log(`  ${icon} [${ch.label}] ${status}${error ? ' — ' + error : ''}`);
  });

  mgr.on('reconnect-start', (name) => {
    console.log(`  🔄 [${mgr._channels[name].label}] 开始重连...`);
  });

  mgr.on('reconnect-success', (name) => {
    console.log(`  ✅ [${mgr._channels[name].label}] 重连成功`);
  });

  mgr.on('reconnect-fail', (name) => {
    console.log(`  ❌ [${mgr._channels[name].label}] 重连失败`);
  });

  mgr.on('reconnect-giveup', (name, msg) => {
    console.log(`  ⛔ [${mgr._channels[name].label}] ${msg}`);
  });

  mgr.on('status-update', (status) => {
    const parts = Object.entries(status).map(([k, ch]) => `${ch.label}=${ch.status}`);
    console.log('  📊 ' + parts.join(' | '));
  });

  mgr.start();

  setTimeout(() => {
    console.log('\n  ' + '═'.repeat(50));
    console.log('  📡 通道状态');
    console.log('  ' + '═'.repeat(50));
    for (const ch of Object.values(mgr.getStatus())) {
      const icon = ch.status === 'online' ? '✅' : ch.status === 'connecting' ? '🔄' : '❌';
      console.log(`  ${icon} ${ch.label.padEnd(12)} ${ch.status.padEnd(10)} ${ch.url || ''}`);
      if (ch.lastError) console.log(`     └─ ${ch.lastError}`);
    }
    console.log('');
  }, 3000);
}

module.exports = ChannelManager;
