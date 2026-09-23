#!/usr/bin/env node
// 🌐 Tunnel Manager — Cloudflare 隧道独立管理模块
'use strict';

const { spawn } = require('child_process');
const fs = require('fs');
const { EventEmitter } = require('events');
const config = require('./config');
const { createLogger } = require('./logger');
const utils = require('./utils');

const logger = createLogger('tunnel');

class TunnelManager extends EventEmitter {
  constructor(options = {}) {
    super();
    this._proc = null;
    this._cfBin = options.cfBin || config.CF_BIN;
    this._state = { status: 'offline', url: '', error: '' };
    this._autoRestart = options.autoRestart !== false;  // 默认开启自动重启
    this._healthTimer = null;
    this._restartTimer = null;
    this._restartCount = 0;
    this._syncFromFile();
  }

  _syncFromFile() {
    this._state.url = utils.readFile('.cf_url');
  }

  getStatus() {
    return { ...this._state };
  }

  async connect() {
    if (this._state.status === 'connecting') {
      return { ok: false, error: '正在连接中' };
    }
    this._restartCount = 0;
    return this._doConnect();
  }

  async _doConnect() {
    this._setState('connecting', '启动中...');
    logger.info('正在连接 Cloudflare...');

    try {
      await utils.killProcess('cloudflared.exe');
      this._proc = null;

      if (!fs.existsSync(this._cfBin)) {
        this._setState('offline', 'cloudflared.exe 未找到');
        return { ok: false, error: 'cloudflared.exe 未找到' };
      }

      const proc = spawn(this._cfBin, ['tunnel', '--url', `http://localhost:${config.PORT}`], {
        windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], detached: true,
      });
      this._proc = proc;

      // 监听进程退出 — cloudflared 崩溃时自动重启
      this._proc.on('exit', (code) => {
        if (this._state.status === 'offline' || !this._proc) return; // 主动断开，不重启
        logger.warn(`cloudflared 意外退出 (code=${code})`);
        this._proc = null;
        if (this._autoRestart && this._state.status !== 'offline') {
          this._scheduleRestart();
        }
      });

      const url = await this._parseUrl(proc);
      if (url) {
        this._setState('online', '', url);
        utils.writeFile('.tunnel_url', url);
        this._startHealthCheck();
        logger.info(`已连接: ${url}`);
        return { ok: true, url };
      }
      this._setState('offline', 'cloudflared 未输出有效 URL');
      logger.error('cloudflared 未输出有效 URL');
      return { ok: false, error: 'cloudflared 未输出有效 URL' };
    } catch (err) {
      this._setState('offline', err.message);
      logger.error(`连接失败: ${err.message}`);
      return { ok: false, error: err.message };
    }
  }

  async disconnect() {
    this._autoRestart = false;
    this._stopHealthCheck();
    if (this._restartTimer) { clearTimeout(this._restartTimer); this._restartTimer = null; }
    if (this._proc) {
      try { this._proc.kill(); } catch { /* 忽略 */ }
      this._proc = null;
    }
    await utils.killProcess('cloudflared.exe');
    utils.writeFile('.cf_url', '');
    utils.writeFile('.tunnel_url', '');
    this._setState('offline', '');
    this._autoRestart = true; // 恢复默认，下次连接可用
    logger.info('已断开 Cloudflare');
    return { ok: true, details: 'Cloudflare 已断开' };
  }

  // ==================== 健康检查 ====================

  _startHealthCheck() {
    this._stopHealthCheck();
    this._healthTimer = setInterval(async () => {
      if (!this._state.url) return;
      try {
        const ok = await utils.checkPort(config.PORT);
        if (!ok) {
          logger.warn('健康检查: 本地服务不可达');
          this._scheduleRestart();
        }
      } catch { /* 静默 */ }
    }, 30000); // 30s 检查一次
  }

  _stopHealthCheck() {
    if (this._healthTimer) {
      clearInterval(this._healthTimer);
      this._healthTimer = null;
    }
  }

  _scheduleRestart() {
    if (this._restartTimer) return; // 已有定时器，避免重复
    this._restartCount++;
    const delay = Math.min(5000 * this._restartCount, 60000); // 5s 起步，最大 60s
    logger.info(`cloudflared ${delay / 1000}s 后自动重连 (第 ${this._restartCount} 次)`);
    this._setState('connecting', `将在 ${delay / 1000}s 后重连...`, this._state.url);
    this._restartTimer = setTimeout(async () => {
      this._restartTimer = null;
      if (this._state.status === 'offline') return; // 用户已手动断开
      await this._doConnect();
    }, delay);
  }

  // ==================== 内部 ====================

  _setState(status, error = '', url = undefined) {
    const prev = this._state.status;
    this._state.status = status;
    this._state.error = error;
    if (url !== undefined) this._state.url = url;
    if (prev !== status) {
      this.emit('change', status, prev, error);
    }
  }

  _parseUrl(proc) {
    const pattern = config.CLOUDFLARE_PATTERN;
    return new Promise(resolve => {
      let buf = '';
      const check = () => {
        const m = buf.match(pattern);
        if (m) { utils.writeFile('.cf_url', m[0]); resolve(m[0]); return true; }
        return false;
      };

      proc.stdout.on('data', chunk => { buf += chunk; check(); });
      proc.stderr.on('data', chunk => { buf += chunk; check(); });

      proc.on('exit', () => setTimeout(() => { if (!check()) resolve(null); }, 500));

      setTimeout(() => {
        if (check()) return;
        try { proc.kill(); } catch {}
        resolve(utils.readFile('.cf_url') || null);
      }, config.TUNNEL_PARSE_TIMEOUT);
    });
  }
}

// ==================== 独立运行 ====================

if (require.main === module) {
  const mgr = new TunnelManager();
  const cmd = process.argv[2];

  if (cmd === 'connect') {
    console.log('  🔄 连接 Cloudflare...');
    mgr.connect().then(r => {
      console.log(r.ok ? `  ✅ ${r.url}` : `  ❌ ${r.error}`);
      process.exit(r.ok ? 0 : 1);
    });
  } else if (cmd === 'disconnect') {
    console.log('  ⏹ 断开 Cloudflare...');
    mgr.disconnect().then(r => {
      console.log(r.ok ? `  ✅ 已断开` : `  ❌ ${r.error}`);
      process.exit(r.ok ? 0 : 1);
    });
  } else if (cmd === 'status') {
    const s = mgr.getStatus();
    console.log(`  ${s.status === 'online' ? '✅' : s.status === 'connecting' ? '🔄' : '⚫'} Cloudflare  ${s.status}${s.url ? ' ' + s.url : ''}`);
    if (s.error) console.log(`     └─ ${s.error}`);
  } else {
    console.log('用法: node src/tunnel-manager.js [connect|disconnect|status]');
  }
}

module.exports = TunnelManager;
