# Workflow onFailure retry model

## 探索背景

- Mohist workflow 正在从内部 policy 列表收敛到用户可读的 YAML 定义。
- 原先展示为 `reactions`，但这个词不能说明触发时机。
- 用户进一步指出 `maxAttempts` 放在 `onFailure` 顶层不准确：真实场景是 `ai-review -> review-passed FAIL -> auto-fix -> ai-review -> review-passed ...` 的循环。

## 关键发现

- Mohist 的失败恢复不是一次性 handler，而是 check 失败后的 repair loop。
- 限制项不应解释为 repair task 的内部 attempt，而应解释为完整 repair loop 次数。
- `then` 不适合暴露给用户，因为它让 `onFailure` 变成嵌套小 workflow；修复后如何继续应该由 workflow engine 的领域规则决定。
- 真实用户问题是：这个 check 失败后会不会自动修、修几轮、用什么上下文修、修不了后何时停下来。

## 可视化

```text
ai-review task
  -> review.md
  -> review-passed check FAIL
  -> onFailure.retry #1
       fix-review-findings
       rerun producer task: ai-review
       rerun check: review-passed
  -> still FAIL
  -> onFailure.retry #2
       fix-review-findings
       rerun ai-review
       rerun review-passed
  -> still FAIL
  -> stop retry, wait for recovery / user
```

## 决策与结论

- 用户 YAML 使用 check-local `onFailure.retry`。
- `retry.limit` 表示完整 repair loop 的最大次数，而不是单个 task 的 process attempt。
- `retry.task` 定义 repair task。
- `retry.inputFrom` 定义 repair task 可读取的失败上下文。
- 不引入 `then`。
- 内部仍可编译到现有 `repairPolicies` / `checkFailurePolicies`，运行时复用现有恢复机制。

## 推荐形态

```yaml
checks:
  - id: review-passed
    title: Review passed
    uses: mohist/verdict
    onFailure:
      retry:
        limit: 2
        task:
          id: fix-review-findings
          title: Fix review findings
          uses: mohist/agent
          with:
            prompt:
              ref: mohist/check/fix-review-findings
        inputFrom:
          - type: failed-check-output
          - type: check-items
            filter: blocking
          - type: snapshot
```

## 开放问题

- 第一版是否要把 `retry.limit` 的默认值设为 1；建议默认 1，用户必须显式写 2 才允许两轮 auto-fix。
- 是否要把 producer task 关系显式建模；建议先由现有 invalidation/producer 规则承担，不放进 YAML。
