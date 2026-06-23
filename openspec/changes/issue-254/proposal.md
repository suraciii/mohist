## Why

CLI 中三个高复杂度大文件（更新命令聚合 1717 行、环境信息采集器 1690 行、表格渲染器 852 行）各自承担多个变更原因，圈复杂度长期居 cli 包前五。它们是典型的"按子命令/按 case 一直往里堆"反模式：加一个更新子命令或采集一项新环境信息，都要在一个巨型文件里找位置，改完还要担心波及并列分支。这是 epic #22（代码复杂度热点治理）的低风险起点——CLI 是纯输出层、无持久化、无跨系统影响，现有命令测试可守护行为。

## What Changes

- 拆分 `MohistCliCommands.Update.cs`：把运行时校验（5 个组件一致性检查）提取为独立**运行时校验器**协作类，把等待/轮询服务可用提取为独立**服务就绪探测器**协作类；更新编排瘦身为只保留公共入口、stage 编排与 Finalize。facade 注入依赖数从 12 项显著下降。
- 拆分 `TableRenderer.cs`：按实体域用 **partial** 拆分为核心（分发 + 共享基础设施：表格写入/JSON 取值/截断）、Issues 簇、Runners 簇、Epics 簇、Entities 簇。单一职责 + 多个 peer case 的正确 partial 用法。
- 拆分 `InfoCollector.cs`：把输出渲染提取为独立**渲染器**协作类（三种输出格式 + 行格式化，仅依赖 TextWriter + InfoResult），把 systemd 单元可用性/uptime/时间戳解析提取为独立**systemd 解析器**（静态、无依赖）。采集器仅保留采集系统信息与选项记录；路径/进程启发式作为采集内部私有静态细节保留。
- 所有 CLI 对外行为（输出格式、参数、退出码、交互）逐字节不变；无新增/删除的子命令或依赖。

## Capabilities

### New Capabilities

- `cli-module-structure`: CLI 高复杂度命令模块的结构组织契约——更新编排 facade 只持有公共入口与 stage 编排，运行时一致性校验与服务就绪探测各自为窄依赖协作类；表格渲染按实体域 partial 拆分，核心只保留分发与共享基础设施；信息采集、输出渲染、systemd 解析分离为三个职责单一的类型。所有不变式可由文件/类结构与构造器依赖面检验。该契约约束未来对这三个模块的改动，避免再次堆出 god class。

### Modified Capabilities

<!-- 无。这是纯内部结构重构：`cli-interface`（覆盖 `mo update` 等命令）与 `cli-info-command`（覆盖 `mo info`）的 spec 级需求不变——输出契约、参数、退出码、交互行为保持逐字节一致，仅文件组织与内部协作关系改变，属实现细节而非 spec 级行为变化。 -->

## Impact

- **受影响代码**：`packages/cli/Mohist.Cli/` 下三个文件及其周边类型——`MohistCliCommands.Update.cs`（含 `UpdateContext` 及嵌套记录）、`InfoCollector.cs`（含 `InfoResult`）、`TableRenderer.cs`。新增 `Update/` 子目录承载提取出的校验器与探测器；表格渲染在原目录按簇拆为多个 partial 文件；信息采集拆出渲染器与 systemd 解析器文件。
- **公共面**：CLI 命令构建层（子命令定义与分发）维持不变；`UpdateContext`、`InfoResult` 等数据记录的公共形状不变。
- **依赖与构造器**：更新 facade 的单一构造器注入从 12 项降至少数 collaborator + 输出；被提取的协作类各自只依赖其所需的部分基础设施（校验器：http/命令执行/文件系统/环境/输出；探测器：http/输出；渲染器：TextWriter + InfoResult）。
- **测试**：现有 CLI 命令测试（`Mohist.Cli.Tests/`）作为行为守护，拆分前后应全部通过且输出一致；不新增对外行为测试，必要时为提取出的可测单元补充 internal 单元测试。
- **无影响项**：server、runner、web、CLI 依赖清单、CLI 运行方式、退出码、输出格式、交互流程。
