#!/usr/bin/env node
// 🧰 共享工具模块 — 所有子模块共用的函数
'use strict';

const fs = require('fs');
const path = require('path');
const net = require('net');
const http = require('http');
const https = require('https');
const os = require('os');
const { exec } = require('child_process');
const config = require('./config');

// ========== 文件 I/O ==========

/** 读文件（从 DATA_DIR），不存在返回空串 */
function readFile(name) {
  try { return fs.readFileSync(path.join(config.DATA_DIR, name), 'utf-8').trim(); }
  catch { return ''; }
}

/** 写文件到 DATA_DIR，静默失败 */
function writeFile(name, text) {
  try {
    const fp = path.join(config.DATA_DIR, name);
    fs.writeFileSync(fp, text, 'utf-8');
  } catch { /* 忽略 */ }
}

// ========== 网络检测 ==========

/** TCP 端口检测 */
function checkPort(port = config.PORT, host = '127.0.0.1', timeout = config.CHECK_TIMEOUT) {
  return new Promise(resolve => {
    const s = net.createConnection(port, host);
    const t = setTimeout(() => { s.destroy(); resolve(false); }, timeout);
    s.on('connect', () => { clearTimeout(t); s.destroy(); resolve(true); });
    s.on('error', () => { clearTimeout(t); s.destroy(); resolve(false); });
  });
}

/** HTTP/HTTPS GET 检测 */
function httpGet(url, timeout = config.CHECK_TIMEOUT) {
  return new Promise(resolve => {
    try {
      const u = new URL(url);
      const mod = u.protocol === 'https:' ? https : http;
      const req = mod.request({
        hostname: u.hostname,
        port: u.port || (u.protocol === 'https:' ? 443 : 80),
        path: u.pathname + u.search,
        method: 'GET',
        timeout,
        rejectUnauthorized: false,
        headers: { 'User-Agent': config.DEFAULT_USER_AGENT },
      }, res => {
        let body = '';
        res.on('data', c => { body += c; });
        res.on('end', () => resolve({ ok: res.statusCode === 200, statusCode: res.statusCode, body }));
      });
      req.on('error', () => resolve(null));
      req.on('timeout', () => { req.destroy(); resolve(null); });
      req.end();
    } catch { resolve(null); }
  });
}

/** 通过默认路由找到真正上网的网卡 IP（换电脑不掉坑） */
function getDefaultRouteIP() {
  try {
    const out = require('child_process').execSync('route print 0.0.0.0', {
      encoding: 'utf-8', timeout: 2000, windowsHide: true,
    });
    // 匹配: 0.0.0.0  0.0.0.0  <gateway>  <interface>  <metric>
    for (const line of out.split('\n')) {
      const m = line.match(/^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+[\d.]+\s+([\d.]+)\s/);
      if (m) return m[1];
    }
  } catch { /* 静默 */ }
  return null;
}

/** 获取局域网 IPv4 — 通过默认路由定位，换了电脑也正确 */
function getLanIP() {
  const ifaces = os.networkInterfaces();
  const SKIP_NAME = /Loopback|Bluetooth|Virtual|VMware|Hyper-V|Docker|vEthernet|WSL/i;
  const routeIP = getDefaultRouteIP();

  const wlanCandidates = [];
  const otherCandidates = [];

  for (const name of Object.keys(ifaces)) {
    if (SKIP_NAME.test(name)) continue;
    for (const net of ifaces[name]) {
      if (net.family !== 'IPv4' || net.internal) continue;
      if (/wlan|wi-?fi|无线/i.test(name)) {
        wlanCandidates.push(net.address);
      } else {
        otherCandidates.push(net.address);
      }
    }
  }

  // 优先级：默认路由 IP → WLAN → 其他 → localhost
  if (routeIP && (wlanCandidates.includes(routeIP) || otherCandidates.includes(routeIP))) {
    return routeIP;
  }
  return wlanCandidates[0] || otherCandidates[0] || 'localhost';
}

// ========== 防火墙 ==========

/** 添加 Windows 防火墙入站规则（需管理员权限，失败静默） */
function addFirewallRule(name, port) {
  const cmd = `netsh advfirewall firewall add rule name="${name}" dir=in action=allow protocol=TCP localport=${port} 2>nul`;
  return execSilent(cmd);
}

/** 检查防火墙规则是否存在（只读，不需要管理员权限） */
function checkFirewallRule(name) {
  return new Promise(resolve => {
    exec(`netsh advfirewall firewall show rule name="${name}"`, {
      windowsHide: true, timeout: 2000,
    }, (err, stdout) => {
      if (err) return resolve(false);
      resolve(stdout.includes(name));
    });
  });
}

// ========== 进程管理 ==========

/** 执行命令，忽略任何错误 */
function execSilent(cmd) {
  return new Promise(resolve => {
    exec(cmd, { windowsHide: true, timeout: 1500 }, () => resolve());
  });
}

/** 按名称杀掉进程 */
function killProcess(name) {
  return execSilent(`taskkill /F /IM ${name} 2>nul`);
}

/** 查找进程是否存在 */
function findProcess(name) {
  return new Promise(resolve => {
    exec(`tasklist /FI "IMAGENAME eq ${name}" /FO CSV /NH`, {
      windowsHide: true, timeout: 3000,
    }, (err, stdout) => {
      if (err) return resolve(false);
      resolve(stdout.includes(name));
    });
  });
}

/** 查找占用指定端口的 PID（Windows netstat） */
function findPidByPort(port) {
  return new Promise(resolve => {
    exec(`netstat -ano | findstr ":${port} " | findstr LISTENING`, {
      windowsHide: true, timeout: 3000,
    }, (err, stdout) => {
      if (err || !stdout.trim()) return resolve(null);
      const lines = stdout.trim().split('\n').filter(l => l.includes('LISTENING'));
      if (lines.length === 0) return resolve(null);
      const parts = lines[0].trim().split(/\s+/);
      resolve(parts[parts.length - 1]);
    });
  });
}

/** 按 PID 杀掉进程 */
function killPid(pid) {
  return execSilent(`taskkill /F /PID ${pid} 2>nul`);
}

// ========== 其他 ==========

/** 休眠 */
function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

/** 获取所有通道的 URL 地址集 */
function getChannelUrls() {
  return {
    lan: `http://${getLanIP()}:${config.PORT}`,
    tunnel: readFile('.tunnel_url'),
  };
}

// ========== 导出 ==========

module.exports = {
  readFile, writeFile,
  checkPort, httpGet, getLanIP, addFirewallRule, checkFirewallRule,
  execSilent, killProcess, findProcess, findPidByPort, killPid,
  sleep, getChannelUrls,
};
