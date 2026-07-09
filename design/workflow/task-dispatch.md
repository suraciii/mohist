# Workflow Task Dispatch

## task.with 展开

模板 YAML 的 `tasks[*].with` 中包含 `${{ }}` 模板表达式。dispatch 时 WorkflowGrain 将其展开为 resolved 变量中的实际值。

```
task.with (模板)                    resolved vars                  dispatched.with
────────────────                    ─────────────                  ───────────────

agent:   "${{ vars.agent }}"  ──→  vars.agent = { type, model } → agent: { type, model }
prompt:  "${{ prompts.x }}"   ──→  prompts.x = "Write a..."     → prompt: "Write a..."
timeout: 600000                                                   → timeout: 600000

规则:
  "${{ path }}" → 从 resolved 变量取值替换
  非模板值      → 原样保留
```

展开后的值如果是 JSON 对象，与 resolved 中的同名 key 进行 deep merge（vars 覆盖，task 级定制保留）。

## 其余 TBD

Dispatch 流程、runner 交互（offer/claim 两阶段、dispatch 快照、恢复）见 [`scheduling.md`](scheduling.md)。check dispatch、ResolveTaskConfig 位置待补。