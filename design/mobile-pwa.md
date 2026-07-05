# Mobile PWA + Push Notification（WIP — 产品方案未定）

> **状态：WIP，暂不实现。** 原 backlog issue #106 已关闭。当前移动端仅有局部适配。后续想清楚移动场景的产品定位后再做。

## 背景

自治工作流的本质是"不用一直盯着"——用户会离开桌面（通勤、开会、睡前、出差）。plan ready 了用户不知道，是 throughput 杀手。移动端访问 + 推送提醒是 self-host 自治系统的 promise 组成部分。

## 原始方案（来自 #106）

走 PWA 路线（self-host 用户的甜蜜点），覆盖核心轻操作场景。

### 场景优先级

1. 🔴 **审批**（plan ready / check ready）—— 阻塞 throughput 的关键
2. 🔴 **观察**（看板、进度、今日完成）
3. 🟡 **干预**（force stop / retry）
4. 🟡 **启动**（从 backlog 挑一个启动）
5. 🟢 **创建**（quick backlog，一句话记想法）

### 交付物设想

1. **PWA 基础设施**：manifest.json + service worker + 安装到主屏 + HTTPS/自签证书支持（没有 HTTPS 就没有 push）
2. **Push notification**：Web Push API + Mohist server 推送触发点（plan ready / check failed / blocked / done）→ 通知点击直达移动端审批页
3. **移动端核心页面**：审批页（单 issue 大按钮）、看板优化、issue detail 简化版
4. **设置端**：移动端只 read-only，复杂配置桌面专属

## 当前状态

- 移动端只有 KanbanBoard 局部适配（`md:hidden`），issue 详情/settings/epics 未适配
- 无 PWA 基础设施（无 manifest.json、无 service worker）
- 无 Web Push 通知机制
- 已有 `mo notify setup`（#352，done）——但那是 Hermes chat-platform 出站通知，不是浏览器内 Web Push

## 后续需要想清楚的问题

1. **移动场景是否真的是核心需求**：Mohist 是"面向个人开发者的本地优先系统"。个人开发者是否真的需要手机审批？还是 desktop + Hermes 聊天通知已经够用？
2. **PWA vs 现有 Hermes 通知**：已有 `mo notify setup` 把审批/完成事件推到 Hermes chat-platform。PWA push 是否与它重复？还是面向不同场景（Hermes=消息流，PWA=直达操作）？
3. **self-host HTTPS 方案**：Web Push 强制 HTTPS。self-host 用户（systemd/Docker/Tailscale）的证书方案要定（mkcert? Caddy reverse proxy? 内置?）
4. **最小可行版本**：如果要做，最简形态——只做 plan-ready push + 移动端审批页？还是先补齐所有页面移动适配？
5. **VAPID key 管理与多设备订阅**：多浏览器订阅的 lifecycle 管理。

## 参考

- 原 issue #106（已关闭）
- #352（done）—— `mo notify setup` Hermes 出站通知
- `docs/self-host.md` —— 部署方案（systemd/Docker/Tailscale，均单节点）
