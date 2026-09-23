#!/usr/bin/env node
// 🪵 统一日志 — 替代裸 console.log
'use strict';

const LEVELS = { debug: 0, info: 1, warn: 2, error: 3 };

const _color = (level) => {
  switch (level) {
    case 'error': return '\x1b[31m'; // red
    case 'warn':  return '\x1b[33m'; // yellow
    case 'debug': return '\x1b[90m'; // gray
    default:      return '';
  }
};
const _reset = '\x1b[0m';

function createLogger(moduleName) {
  const name = (moduleName || 'app').padEnd(10);
  return {
    debug: (msg) => _write('debug', name, msg),
    info:  (msg) => _write('info',  name, msg),
    warn:  (msg) => _write('warn',  name, msg),
    error: (msg) => _write('error', name, msg),
  };
}

function _write(level, name, msg) {
  if (!msg) return;
  const color = _color(level);
  // 统一格式: `  [模块名] 信息` （兼容原版前缀风格）
  const prefix = `  ${color}[${name.trim()}]${_reset} `;

  if (level === 'error') {
    console.error(prefix + msg);
  } else {
    console.log(prefix + msg);
  }
}

module.exports = { createLogger };
