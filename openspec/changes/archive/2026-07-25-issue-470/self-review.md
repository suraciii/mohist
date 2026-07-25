# Self-Review - Issue 470

已对照当前 issue 470 审查 `proposal.md`、`design.md`、`tasks.json` 和三个
capability spec，并抽查现有 Server 启动、OTLP 摄取、Agent read path、后台工作、
OTel 配置与测试边界。

## Verdict

未发现阻塞构建的问题。计划已经把 issue 的产品要求收敛为明确且可验证的实现契约，
任务拆分也具备可执行的依赖顺序。

## Review Notes

- 三态状态、固定成本状态读取、进程与存储压力、四类 telemetry outcome、有界五分钟
  路由摘要、转换日志、core health 独立性均有对应 spec、设计决策和任务验收项。
- 指标 catalog 固定了 instrument、kind、unit、完整 label key 集合及低基数值域；本地摘要
  与 `Meter` 使用同一事实入口，且未把指标导回内置 collector。
- OTel endpoint、storage probe、maintenance 和 exporter 的反馈隔离边界明确；detached
  request-scope 泄漏覆盖两个现有 `Task.Run` 路径及三个 store 共用的 dispatcher poke。
- 摄取计划对 commit、non-retryable loss、retryable rollback 和 transaction 内取消分别定义了
  唯一 outcome、计数、HTTP/OTLP wire behavior 与 degradation 语义，不再把取消误报为写失败。
- fallback 复用同一 host plan，保留 enabled intent 与 runtime epoch；listener bind failure 的
  分类输入不再与 outbound exporter endpoint 混淆，后续故障替换 latest reason 的规则一致。
- Agent status/activity 的 canonical 与 compatibility routes 共用 project-resolved handler；selector
  优先级、无 selector 的 400、off 状态下的 response-local counting 及精确字段语义均已锁定。
- `tasks.json` 为有效 JSON，8 个任务依赖无环，所有 spec anchors 存在；测试计划遵守 fake time、
  无真实外部依赖、操作次数断言和固定响应/内存上限约束。

## Residual Implementation Risks

实现阶段仍需严格维持全局状态锁的短临界区、ambient scope 的 close/suppress 顺序、OTLP
JSON/protobuf wire parity，以及 fallback 的 stop/dispose 异常顺序。这些风险均已有明确任务验收
项和确定性测试要求，不构成计划缺口。

<promise>PASS</promise>
