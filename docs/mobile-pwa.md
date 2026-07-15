---
status: wip-not-implemented
---

# Mobile PWA + Push Notification

> 原 backlog issue #106 已关闭。本文件记录产品方案的开放问题。

## 背景

自治工作流的本质是"不用一直盯着"——用户离开桌面时，plan ready 了不知道是 throughput 杀手。移动端访问 + 推送提醒是 self-host 自治系统的 promise 组成部分。

## 原始方案（#106）

走 PWA 路线，场景优先级：

1. 审批（plan ready / check ready）
2. 观察（看板、进度）
3. 干预（force stop / retry）
4. 启动（从 backlog 启动）
5. 创建（quick backlog）

交付物设想：PWA 基础设施（manifest + service worker）、Web Push、移动端核心页面（审批页、看板优化、issue 简化版）、设置端桌面专属。

## 当前状态

- 仅 KanbanBoard 局部适配（`md:hidden`），其他页面未适配
- 无 PWA 基础设施、无 Web Push
- 已有 `mo notify setup`（#352，Hermes chat-platform 出站通知）——但那是聊天通知，不是浏览器内 Web Push

## 后续需想清的问题

1. **移动场景是否真的是核心需求**：个人开发者是否真的需要手机审批？还是 desktop + Hermes 聊天通知已够用？
2. **PWA vs Hermes 通知**：两者是否重复——Hermes = 消息流，PWA = 直达操作？
3. **self-host HTTPS**：Web Push 强制 HTTPS，证书方案待定（mkcert？Caddy？内置？）
4. **最小可行版本**：只做 plan-ready push + 移动端审批页？还是补齐所有页面移动适配？
5. **VAPID key 与多设备订阅管理**。
