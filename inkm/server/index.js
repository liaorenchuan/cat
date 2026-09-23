#!/usr/bin/env node
// 📱 Inkm Server — 入口文件
'use strict';

const { createLogger } = require('./src/logger');
const app = require('./src/server/app');

const logger = createLogger('server');

// ========== 退出清理 ==========

process.on('SIGINT', async () => {
  await app.stop();
  process.exit(0);
});
process.on('SIGTERM', async () => {
  await app.stop();
  process.exit(0);
});

// ========== 启动 ==========

(async () => {
  try {
    await app.start();
  } catch (err) {
    logger.error(`启动失败: ${err.message}`);
    process.exit(1);
  }
})();
