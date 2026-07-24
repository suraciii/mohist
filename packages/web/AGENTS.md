# packages/web — 贡献规则

Scope：`packages/web/`（React 19 + Vite + TanStack Query）。改动本目录前读完本文件。

## The one rule

**依赖只许沿层级向下**：`shared → entities → features → widgets → pages → app`，高层级可以 import 低层级，反之禁止。由 `npm run check:fsd -w packages/web` 强制，CI 会挡。

## 规则

1. **跨 slice 访问走公共出口**。slice 根部的 `index.ts` 是它唯一的公共 API；禁止 import slice 内部文件。新增导出时先改 slice 的 `index.ts`，而不是在调用方加深路径。
2. **entities 跨 slice 引用用 `@x` 记法**（如 `entities/issue/@x/workflow`），不直接 import 对方实体内部。
3. **新代码先选层级再放文件**：纯工具/基础 UI 放 `shared`，领域模型放 `entities`，用户操作放 `features`，组合块放 `widgets`，路由页放 `pages`。拿不准就先放低层级，升级容易降级难。
4. **改完跑三样**：`npm run typecheck -w packages/web`、`npm run test:run -w packages/web`、`npm run check:fsd -w packages/web`。

## 指针

- 产品行为 spec：[`docs/web-ui.md`](../../docs/web-ui.md)
- 设计 spec：[`design/web-ui.md`](../../design/web-ui.md)
- 测试约束（fake 入口、禁真实依赖）：[`design/testing.md`](../../design/testing.md)
