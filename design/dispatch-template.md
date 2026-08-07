# Agent dispatch 任务模板

派发本仓库开发任务给外部 agent（herdr / pi）时的可复用派单约定，固化三条硬规则：
model fallback 链、测试命令 timeout、完成定义。

只规范化"派单怎么发"；不替换 herdr / subagent 机制本身，不约束 Mohist Workflow 内部的
stage model 与 task dispatch（那是 Workflow Definition 的职责）。

## Model fallback 链

| 档位 | 语义 | 当前模型 | 何时用 |
|---|---|---|---|
| 0 廉价默认 | 大多数任务 | `opencode-go/deepseek-v4-flash` | 默认 |
| 1 廉价备选 | 档 0 探活失败 | `minimax/MiniMax-M3`（备选池：`kimi-coding/k3`） | 档 0 不可用 |
| 2 宝贵 | 最后手段 | `zai-coding-cn/glm-5.2` | 前两档全挂，prompt 显式标注"宝贵智力资源" |

- 模型名必须带 provider 前缀。`glm-5.2` / `deepseek-v4-flash` 裸名在多个 provider 间
  歧义：pi 解析报错 → TUI 不就绪 → herdr 启动超时无响应。
- 派单前必须探活（见下节）。探活失败直接跳下一档；禁止在同一 provider 上重试或无限等待。
- 档 1 是"廉价池 + 探活"，不是写死的单一模型。池子随配额与可用性漂移（
  `volcengine-ark/deepseek-v4-flash` 同型号曾是备选，2026-08 配额耗尽 429 不可用），
  以探活结果为准，不赌。
- glm-5.2 是宝贵智力资源：有廉价档可用就不用；不得拿它跑本该廉价的有界任务；用时在
  派单 prompt 中显式标注"宝贵智力资源"，并给任务设时间盒。

## 探活

派单前探活 provider，不赌 broken provider。依据（真实经验）：

- deepseek-v4-flash（opencode-go）间歇 503：连续多轮宕机、启动即失败；曾因此回退到
  glm-5.2 跑一个本该廉价的任务，还跑了约 24 分钟。
- glm-5.2（zai-coding-cn）实测可靠。
- 裸模型名歧义 → pi 报错 → herdr 启动超时无响应。

命令：

```bash
timeout 15s pi --no-session --model <provider/model> -p 'ok' >/dev/null 2>&1
```

- 退出码 0 = 可用；非零或超时 = 不可用，直接跳下一档。
- 探活不写会话（`--no-session`）、不占 pane、可并行探多档。实测可用模型 3–5s 返回；
  不可用模型快速报错（503 / 429）或被 15s 超时截停，两种结局都触发跳档。
- 探活通过 ≠ 全程可用：执行中启动即失败（连续 503）时同样换档重派，不赌。
- 用 herdr 启动时，`herdr agent start --timeout <MS>` 保持有界（默认 30s 即可）：
  启动不就绪即失败，本身就是一道探活；失败后换档重派，禁止无限重试同一 provider。

## 测试命令 timeout

所有会跑测试的派单命令必带 `timeout`，禁止裸跑 `dotnet test` / `npm test` / vitest。

```bash
timeout -k 10s <N>s <command>
```

超时先发 TERM，10 秒后升 KILL，防止残留子进程继续拖住后续命令。

建议值（对齐 [testing.md](testing.md) 的时长预算）：

| 命令 | timeout | 依据 |
|---|---|---|
| focused unit/arch（`npm run test:budget -- focused …` / xUnit apphost `-class` / vitest 单文件） | `120s` | unit p95 ≤ 50ms、绝对 ≤ 500ms，2 分钟含冷启动绰绰有余 |
| focused spec（单个 Specs class / vitest spec 文件） | `180s` | spec p95 ≤ 500ms、绝对 ≤ 5s |
| 全量套件（`npm test` / `npm run test:budget`） | `480s` | guard 自身 5 分钟 hard deadline；冗余覆盖 guard 之外的点（restore / build / 进程启动） |
| TS typecheck（`npm run typecheck -w …`） | `180s` | 非测试，但同样可能挂，一并包住 |

- timeout 杀掉 = 该命令失败（hang 或超预算），必须诊断或缩小范围重跑；不得把被杀当作通过。
- 完成报告写真实退出码，不写"应该过了"。

## 完成定义

三件缺一不算完成：

1. **build 过** —— 派单任务要求的构建命令（如 `npm run build`）退出码 0。
2. **相关测试过** —— 改动对应的 typecheck + 测试通过，含 `npm run test:budget`
   不新增违规；跑过的命令与结果摘要写进报告。
3. **PR 已开** —— 分支已推、PR 已建（进 master），正文含改动说明与验证证据。

完成报告必须附三件证据（构建命令 + 退出码、测试命令 + 结果摘要、PR 链接）；证据缺失
即未完成。依据：曾出现"build 过就报完成"的半成品——build 过但测试没跑或 PR 未开，
无法合并，派单方还得回头补。

## 派单 prompt 骨架

```text
任务：<issue / 需求 + 验收>
工作区：<worktree 路径>
模型：<档位 + provider 全名，探活已过>
规则：
- 所有测试命令必带 timeout（unit 120s / spec 180s / 全量 480s）
- 完成定义：build 过 + 相关测试过 + PR 已开，缺一不算完成；报告附三件证据
- 宝贵智力资源：本任务使用 zai-coding-cn/glm-5.2，保持有界，不蔓延（仅档 2 时）
```

## Status

- 本模板是约定，无自动 enforcement：探活、timeout、完成定义靠派单人纪律。自动化的
  可选方向（派单前强制探活的脚本、超时注入）另行评估。
- 档位表是当前本地模型池快照；池子变化时更新档位表，规则不变。
