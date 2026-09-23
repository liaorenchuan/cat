const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('electronAPI', {
  // 窗口控制
  minimize: () => ipcRenderer.send('win-minimize'),
  maximize: () => ipcRenderer.send('win-maximize'),
  close:    () => ipcRenderer.send('win-close'),

  // 服务器控制
  serverStart: () => ipcRenderer.invoke('server-start'),
  serverStop:  () => ipcRenderer.invoke('server-stop'),
  serverStatus:() => ipcRenderer.invoke('server-status'),

  // 通道管理
  channelStatus:  () => ipcRenderer.invoke('channel-status'),
  channelConnect: (name) => ipcRenderer.invoke('channel-connect', name),
  channelDisconnect:(name) => ipcRenderer.invoke('channel-disconnect', name),

  // 实时通道状态推送（主进程每 2 秒推送）
  onChannelStatus: (callback) => {
    ipcRenderer.on('channel-status', (_, data) => callback(data));
  },

  // 剪切板
  copyText: (text) => ipcRenderer.invoke('clipboard-write', text),
});
