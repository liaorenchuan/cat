#!/usr/bin/env node
// 🧩 HTTP 中间件 — JSON 解析、静态文件、错误处理
'use strict';

const fs = require('fs');
const path = require('path');
const config = require('../config');

// ========== JSON 请求体解析 ==========

/** 解析 POST JSON 请求体，挂到 req.body */
function parseBody(req, res, next) {
  if (req.method !== 'POST') return next();
  let body = '';
  let size = 0;
  let aborted = false;
  req.on('data', c => {
    if (aborted) return;
    size += c.length;
    if (size > config.BODY_LIMIT) {
      aborted = true;
      res.writeHead(413, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ok: false, error: 'Request body too large' }));
      req.destroy();
      return;
    }
    body += c;
  });
  req.on('end', () => {
    if (aborted) return;
    try {
      req.body = body ? JSON.parse(body) : null;
    } catch {
      req.body = null;
    }
    next();
  });
}

// ========== 静态文件服务 ==========

const PUBLIC_DIR = path.join(config.PHONE_DIR, 'public');

const INDEX_FILES = new Set(['/', '/desktop']);

function serveStatic(req, res, next) {
  if (req.method !== 'GET') return next();

  let pn = req._parsedUrl.pathname;
  if (INDEX_FILES.has(pn)) pn = '/index.html';

  const fp = path.join(PUBLIC_DIR, pn);
  // 安全检查：确保文件在 public 目录下
  if (!fp.startsWith(PUBLIC_DIR)) {
    return next();
  }

  fs.readFile(fp, (err, data) => {
    if (err) return next();
    res.writeHead(200, {
      'Content-Type': config.MIME.get(path.extname(fp)) || 'application/octet-stream',
      'Cache-Control': 'no-cache, no-store, must-revalidate',
      'Pragma': 'no-cache',
      'Expires': '0',
    });
    res.end(data);
  });
}

// ========== 统一 JSON 响应 ==========

function jsonOk(res, data) {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(data || { ok: true }));
}

function json400(res) {
  res.writeHead(400, { 'Content-Type': 'application/json' });
  res.end('{}');
}

// ========== 统一错误处理 ==========

function errorHandler(err, req, res) {
  if (!res.headersSent) {
    res.writeHead(500, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: false, error: err.message || 'Internal error' }));
  }
}

// ========== 导出 ==========

module.exports = {
  parseBody,
  serveStatic,
  jsonOk,
  json400,
  errorHandler,
};
