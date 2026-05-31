# Workflow Artifact And Marker Model

## 探索背景
- Mohist workflow 的用户输入会是 YAML，因此内置默认 workflow 也必须能用同一套 YAML 语义表达。
- 之前的 `resultContract` 把 task 产出、agent 输出协议、check 判定混在一起，尤其 `promise-marker` 看起来像所有 task 都通用，但真实使用主要发生在 agent artifact 文本里。

## 关键发现
- Artifact 是 workflow 级的路径资源，不是 task 参数。Task 不应该声明自己拥有某个 artifact，也不需要 `writes` 这种绑定 DSL。
- Task 只是通过 prompt 和普通字符串路径使用 artifact，例如 `{{ artifacts.openspecChange }}/design.md`。
- Plan 阶段的多个 task 共同写入同一个 OpenSpec change 目录，每个 task 只生成目录里的部分文件；因此不能把 proposal/design/specs/tasks 都建模成独立 artifact。
- Promise marker 有两个不同场景：
  - `mohist/agent` 的执行输入：要求 agent 在某个 path 中写出用户定义的 marker 集合；如果缺失，agent executor 可以复用/继续 session 追加提示补齐。
  - check 的只读验证：读取某个 path，判断文本中是否包含期望 marker。

## 可视化
```
workflow.artifacts
  openspecChange = "{{ openspec.changeDir }}"
        │
        ├─ prompt vars / inline prompt
        │    "{{ artifacts.openspecChange }}/design.md"
        │
        ├─ mohist/agent.with.requiredMarkers
        │    path: "{{ artifacts.openspecChange }}/self-review.md"
        │    markers: ["<promise>PASS</promise>", "<promise>FAIL</promise>"]
        │    onMissing: continue-session
        │
        └─ mohist/marker check
             path: "{{ artifacts.openspecChange }}/self-review.md"
             expect: "<promise>PASS</promise>"
```

## 决策与结论
- 保留 workflow 级 `artifacts`，但它只是可插值路径变量集合。
- 不引入 `task.outputs`、`task.artifacts`、`task.writes`。
- `mohist/agent.with.requiredMarkers` 是 agent executor 专属输入，用于声明 marker 枚举和缺失补齐策略。
- `mohist/marker` 是只读 check，用于检查 path 中的 marker。
- 现有 `resultContract` 应迁移为兼容层或内部派生，不再作为默认 YAML 的主要表达。

## 开放问题
- 是否需要把 `mohist/verdict` 完全改名为 `mohist/marker`，还是短期保留 `mohist/verdict` 作为语义别名。
- marker parser 是否只支持全文 contains，还是需要支持 “必须唯一 / 必须在最后一行” 等策略。
