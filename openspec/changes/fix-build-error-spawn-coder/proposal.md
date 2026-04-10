## Why

`packages/cli` 的 TypeScript 编译失败，错误为 `spawn-coder.ts:78` 调用了不存在的 `runAcpOneshot` 函数。这导致整个后端无法构建，新增的 providers API 路由无法生效，web UI 的 Settings 页面因 API 返回 HTML 而报错。

## What Changes

- 修复 `spawn-coder.ts` 中对不存在函数 `runAcpOneshot` 的调用，改为使用已有的 `runAcpSession`
- 确保 `tsc` 编译通过，后端和前端均可正常构建

## Capabilities

### New Capabilities

（无）

### Modified Capabilities

- `spawn-coder`: 修复编译错误，将 `runAcpOneshot` 调用替换为 `runAcpSession`

## Impact

- `packages/cli/src/tools/spawn-coder.ts` — 主要修改文件
- `npm run build` — 恢复正常编译
- `npm run server` — providers API 路由可正常响应
