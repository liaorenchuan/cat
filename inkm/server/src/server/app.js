#!/usr/bin/env node
// 🌐 HTTP 服务器组装 — 中间件链 + 生命周期
'use strict';

const http = require('http');
const config = require('../config');
const { parseBody, serveStatic, errorHandler } = require('./middleware');
const { matchRoute } = require('./router');
const utils = require('../utils');
const { createLogger } = require('../logger');
const keyboard = require('../input/keyboard');
const mouse = require('../input/mouse');
const sendinput = require('../input/sendinput');

const logger = createLogger('server');

// ========== 服务器对象 ==========

const server = http.createServer((req, res) => {
  try {
    // 中间件链：解析 body → 路由匹配 → 静态文件 → 404
    parseBody(req, res, () => {
      const route = matchRoute(req);
      if (route) return route(req, res);

      serveStatic(req, res, () => {
        res.writeHead(404);
        res.end('Not found');
      });
    });
  } catch (err) {
    errorHandler(err, req, res);
  }
});

// ========== 启动 ==========

/** 确保端口空闲（最多等 maxWait ms） */
async function waitPortFree(port, maxWait = 8000) {
  const start = Date.now();
  while (Date.now() - start < maxWait) {
    const pid = await utils.findPidByPort(port);
    if (pid) {
      logger.info(`发现旧进程 PID ${pid} 占用了端口 ${port}，正在终止...`);
      await utils.killPid(pid);
    }
    const busy = await utils.checkPort(port);
    if (!busy) return true;
    await utils.sleep(300);
  }
  return false;
}

/** 启动服务器 */
async function start(options = {}) {
  const port = options.port || config.PORT;

  // 1. 清理残留进程
  await utils.killProcess('mouse-daemon.exe');

  // 2. 确保端口空闲
  const free = await waitPortFree(port, options.waitPort || 8000);
  if (!free) {
    logger.error(`端口 ${port} 无法释放，请检查是否有其他程序占用`);
    throw new Error(`Port ${port} not free`);
  }

  // 3. 添加防火墙规则（静默，需管理员权限）
  utils.addFirewallRule('Inkm 智能键鼠', port);

  // 4. 先打开 HTTP 端口（让桌面端立刻检测到）
  await new Promise((resolve, reject) => {
    server.listen(port, '0.0.0.0', () => {
      logger.info(`Inkm 已启动 (端口 ${port})`);
      logger.info(`局域网: http://${utils.getLanIP()}:${port}`);
      const u = utils.readFile('.tunnel_url');
      if (u) logger.info(`外网:   ${u}`);
      resolve(server);
    }).on('error', err => {
      logger.error(`服务器启动失败: ${err.message}`);
      reject(err);
    });
  });

  // 5. 后台初始化鼠标和输入模块（不阻塞启动）
  mouse.start();
  mouse.detectScreens(600);

  sendinput.init();
  if (config.USE_SENDINPUT) {
    sendinput.enable();
    sendinput.detectScreens().then(screens => {
      if (screens) {
        logger.info(`SendInput: 检测到 ${screens.length} 个显示器`);
      }
    });
  }
  logger.info('鼠标常驻服务运行中');

  return server;
}

// ========== 停止 ==========

/** 优雅停止 */
async function stop() {
  logger.info('正在停止...');
  mouse.stop();
  sendinput.disable();
  // 快速释放端口
  const port = config.PORT;
  for (let i = 0; i < 5; i++) {
    const pid = await utils.findPidByPort(port);
    if (pid) {
      await utils.killPid(pid);
    } else {
      const busy = await utils.checkPort(port);
      if (!busy) break;
    }
    await utils.sleep(200);
  }
}

module.exports = {
  start,
  stop,
  server,
};
