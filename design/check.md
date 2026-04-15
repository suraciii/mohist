# CHECK Stage

## 职责

6-pass 代码审查 + 跨切面审计 + 对比需求。

CHECK stage 提供独立于 BUILD 的反馈视角，是循环模型的关键反馈点。

## 核心定位

CHECK stage 的审查者是**对手**，不是同事。职责是对抗性地检验 Plan 和 Build 阶段的产出，专门找问题。

## 两层审查

### Layer 1: 6-Pass 代码审查 (per-task/per-diff)

| Pass | 关注点 | 检查内容 |
|------|--------|---------|
| 1. Logic errors | 逻辑正确性 | 死循环、off-by-one、null 解引用、死代码、布尔逻辑错误、竞态条件 |
| 2. Operation ordering | 操作顺序 | 副作用在 guard 之前、mutation 在验证之前、资源未释放、audit log 位置 |
| 3. Bad practices | 实践质量 | 未验证输入、过宽异常处理、缺少 I/O 错误处理、不安全类型转换 |
| 4. Security | 安全 | SQL 注入、硬编码密钥、敏感数据暴露 |
| 5. Magic strings/values | 魔法值 | 内联字面量、重复字面量、应提取为常量/枚举 |
| 6. Pattern improvements | 模式改进 | 硬编码依赖 → DI、条件链 → 策略模式、过程代码 → 命名抽象 |

输出格式：Location + Severity (Critical/High/Medium/Low) + Pass + Description + Fix

来源: Mario Barbero 的 code-review skill

### Layer 2: 跨切面审计 (whole-feature)

读完所有变更代码后审查：

| 审计维度 | 检查内容 |
|---------|---------|
| Consistency | 模块间命名、错误处理、相似操作是否一致 |
| Security | 完整 surface area 上的输入验证、认证授权、敏感数据、注入风险 |
| Logic | 竞态、失败模式、边界条件、与验收标准的匹配 |
| Best practices | 重复逻辑、深模块 vs 浅模块、职责过多、测试是否脆弱 |

关键原则：**先读完所有代码建心智模型，再审计**。不是逐文件挑错，是跨文件找系统性问题。

来源: Mario Barbero 的 final-audit skill

## 审查维度

审查维度是审查的侧重点，不是角色分工。M1/M2 单 agent 执行全部维度。M3 可考虑多 agent 并行加速。

```
CHECK {
  jobs: [
    { agent: "reviewer", task: "对抗性审查（6-pass + 跨切面审计）" }
  ]
  gate_after: human
}
```

## 工具集

- `read`: 阅读代码变更、需求文档、specs
- `bash`: 运行测试套件

## 产出物

- 审查报告（各 pass 发现 + 跨切面审计发现）
- 问题列表（按 severity 分组: Critical / High / Medium / Low）
- 整体评估（是否可以留在生产环境）

## 循环机制

CHECK 完成后两种路径：

1. **通过** → Issue stage 变为 `done`
2. **有问题** → Issue stage 从 `check` 回到 `plan`，PLAN 基于审查报告制定修复计划

这个循环对应 DevOps pipeline 的反馈周期：CHECK 发现实现与方案的偏差 → 回到 PLAN 重新规划 → BUILD 重新实现 → CHECK 再次检查。

## Gate

默认配置 `gate_after: human`：将审查报告展示给用户，等待批准或反馈。

- 批准 → Issue 完成，`done`
- 给反馈 → 回到 PLAN 修复
- 整体评估：是否可以留在生产环境
