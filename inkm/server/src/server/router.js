#!/usr/bin/env node
// 🛣️ 路由定义 — 所有 API 端点
'use strict';

const keyboard = require('../input/keyboard');
const mouse = require('../input/mouse');
const sendinput = require('../input/sendinput');
const config = require('../config');
const { jsonOk, json400 } = require('./middleware');
const utils = require('../utils');

// 根据配置选择输入模块
function input() {
  return config.USE_SENDINPUT && sendinput.getStatus().enabled ? sendinput : null;
}

// 带后备的鼠标/键盘操作
function sendMouse(cmd, ...args) {
  const si = input();
  if (si) {
    switch (cmd) {
      case 'm': return si.mouseMove(args[0], args[1]);
      case 'g': return si.mouseGoto(args[0], args[1]);
      case 'c': return si.leftClick();
      case 'd': return si.doubleClick();
      case 'r': return si.rightClick();
      case 's': return si.scroll(0, args[0]);
      case 'h': return si.scroll(args[0], 0);
      case 't': return si.dragStart();
      case 'u': return si.dragEnd();
    }
  }
  mouse.send(cmd + (args.length ? ' ' + args.join(' ') : ''));
}

function sendKeyboard(action, text) {
  const si = input();
  if (si) {
    switch (action) {
      case 'text': return si.typeTextEnter(text);   // 打字+回车
      case 'text-noenter': return si.typeText(text); // 仅打字
      case 'enter': return si.enter();
      case 'backspace': return si.backspace();
      case 'keys': return si.pressKey(text);
    }
  }
  // 回退到原始模块
  switch (action) {
    case 'text': return keyboard.sendText(text);
    case 'text-noenter': return keyboard.sendTextNoEnter(text);
    case 'enter': return keyboard.sendEnter();
    case 'backspace': return keyboard.sendBackspace();
    case 'keys': return keyboard.sendKeys(text);
  }
}

// ========== POST 快捷包装 ==========

function POST(handler) {
  return (req, res) => {
    if (req.method !== 'POST') return json400(res);
    handler(req, res);
  };
}

// ========== 路由表 ==========

const ROUTES = {

  // ---- 文字输入 ----

  '/send': POST((req, res) => {
    const text = req.body?.text?.trim();
    if (!text) return json400(res);
    sendKeyboard('text', text);
    jsonOk(res);
  }),

  '/send-typing': POST((req, res) => {
    const text = req.body?.text?.trim();
    if (!text) return json400(res);
    sendKeyboard('text-noenter', text);
    jsonOk(res);
  }),

  // ---- 按键 ----

  '/key': POST((req, res) => {
    const action = req.body?.action || '';
    const keys = req.body?.keys || '';
    if (keyboard.sendAction(action)) return jsonOk(res);
    if (keys) {
      sendKeyboard('keys', keys);
      return jsonOk(res);
    }
    json400(res);
  }),

  // ---- 鼠标 ----

  '/scroll': POST((req, res) => {
    if (!req.body) return json400(res);
    const dy = parseInt(req.body.dy) || 0;
    const dx = parseInt(req.body.dx) || 0;
    sendMouse('s', dy);
    sendMouse('h', dx);
    jsonOk(res);
  }),

  '/mouse-move': POST((req, res) => {
    if (!req.body) return json400(res);
    sendMouse('m', parseInt(req.body.dx) || 0, parseInt(req.body.dy) || 0);
    jsonOk(res);
  }),

  '/mouse-goto': POST((req, res) => {
    if (!req.body) return json400(res);
    sendMouse('g', parseInt(req.body.x) || 0, parseInt(req.body.y) || 0);
    jsonOk(res);
  }),

  '/mouse-click': POST((req, res) => {
    if (!req.body) return json400(res);
    const type = req.body.type === 'double' ? 'd' : req.body.type === 'right' ? 'r' : 'c';
    sendMouse(type);
    jsonOk(res);
  }),

  // ---- 快捷操作 ----

  '/rightclick': (req, res)  => { sendMouse('r'); jsonOk(res); },
  '/backspace':  (req, res)  => { sendKeyboard('backspace'); jsonOk(res); },
  '/delete':     (req, res)  => { keyboard.sendDeleteLine(); jsonOk(res); },
  '/enter':      (req, res)  => { sendKeyboard('enter'); jsonOk(res); },
  '/drag-start': (req, res)  => { sendMouse('t'); jsonOk(res); },

  // ---- 状态 ----

  '/status': (req, res) => {
    jsonOk(res, {
      running: true,
      pid: process.pid,
      screens: mouse.getScreenInfo(),
      inputMode: config.USE_SENDINPUT ? 'sendinput' : 'legacy',
      sendinput: sendinput.getStatus(),
    });
  },

  // ---- SendInput 模式切换 ----

  '/sendinput-on': (req, res) => {
    config.USE_SENDINPUT = true;
    sendinput.init();
    sendinput.enable();
    sendinput.detectScreens().catch(() => {});
    jsonOk(res, { ok: true, mode: 'sendinput' });
  },

  '/sendinput-off': (req, res) => {
    config.USE_SENDINPUT = false;
    sendinput.disable();
    jsonOk(res, { ok: true, mode: 'legacy' });
  },

  '/sendinput-status': (req, res) => {
    jsonOk(res, sendinput.getStatus());
  },

  '/calibrate': (req, res) => {
    const screen = mouse.getPrimaryScreen();
    if (!screen) return json400(res);
    const rx = screen.x + screen.w, ry = screen.y;
    const cx = screen.x + Math.round(screen.w / 2), cy = screen.y + Math.round(screen.h / 2);
    mouse.send('g ' + (rx - 10) + ' ' + (ry + 10));
    setTimeout(() => { mouse.send('c'); }, 100);
    setTimeout(() => { mouse.send('g ' + cx + ' ' + cy); }, 250);
    jsonOk(res, { topRight: [rx - 10, ry + 10], center: [cx, cy] });
  },

  '/urls': (req, res) => {
    jsonOk(res, {
      lan: `http://${utils.getLanIP()}:${config.PORT}`,
      tunnel: utils.readFile('.tunnel_url'),
    });
  },
};

// ========== 路由匹配 ==========

function matchRoute(req) {
  const url = new URL(req.url, 'http://' + req.headers.host);
  req._parsedUrl = url;
  return ROUTES[url.pathname] || null;
}

module.exports = { matchRoute, ROUTES };
