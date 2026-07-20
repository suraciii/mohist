# 浏览器、冒烟与端到端验收规划

> Status: Planned. 本文只记录 Mohist 仓库的工程规划；当前不修改 CI、Project Workflow 配置或测试执行方式。

## 范围

这套实践只适用于 Mohist 仓库自身。Mohist 产品继续提供通用的 Workflow Profile、Task、Check 和 Action，用户可以采用不同的测试与交付实践。

浏览器测试、冒烟测试和端到端测试是不同测试类型。未来 Workflow 可以把它们组织在同一个验收阶段中，但不把现有 browser suite 描述成端到端测试。

## 当前状态

- 默认 `npm test` 不运行 browser suite。
- GitHub CI 继续运行现有 browser suite，直到新的验收路径经过真实 Workflow canary。
- `mohist-local` 项目继续使用 builtin `mohist/github-pr`，没有项目专属 acceptance 配置。

## 目标状态

- GitHub 自动 CI 只承担低成本、快速、可大批量运行的构建、架构、unit、spec 和 integration 验证。
- 浏览器、冒烟和端到端验证由本项目 Workflow 的 Check 阶段作为独立验收项运行。
- 验收失败阻止 Check 进入审批；审批反馈修改代码后，验收必须重新执行。
- 验收命令不得留下 Git 可见的工作区改动。
- builtin Workflow、Workflow DSL 和通用 Action 不因本项目实践而改变。

## 方案边界

项目专属 Workflow Template 是首选配置边界；它引用项目变量决定实际执行内容。项目模板 ID 只需稳定且在项目内唯一，命名不参与验收行为。

验收项倾向使用 stage check，因为审批反馈会使 checks 失效并重新执行。实施前必须解决或明确以下问题：

- Check 执行路径当前不具备 task 的 branch-stability、clean-worktree、artifact 和 recovery 后置能力。
- 需要确定失败日志与浏览器 trace 的保留位置。
- 首个版本是否仅阻断并人工修复，还是需要独立的自动恢复能力。
- Runner 的浏览器环境属于一次性运行环境准备，不应在每次验收中安装。

如果这些问题需要修改通用 Workflow 语义，应先重新评估 Task 与 Check 的取舍，不为本项目需求静默扩展产品模型。

## 实施门槛

后续实施必须按以下顺序推进：

1. 保留现有 GitHub browser gate。
2. 创建项目专属模板和验收变量，但先不删除旧 gate。
3. 用新的 WorkflowRun 在可重建 workspace 中完成一次真实 canary。
4. 通过审批反馈修改 canary，并确认验收再次执行。
5. 验证失败能阻止审批、成功不会留下 Git 可见改动，并能取得可行动的失败证据。
6. 以上条件全部满足后，才从 GitHub CI 移除 browser suite。

回滚只切换项目默认模板；builtin `mohist/github-pr` 始终保留。已经启动的 WorkflowRun 使用自己的 Definition snapshot，不作为模板切换验证对象。

## 非目标

- 本阶段不新增、迁移或重写 browser、smoke 或 end-to-end 测试。
- 本阶段不创建 Project Workflow Template，不设置 live variables，不切换默认模板。
- 本阶段不修改 GitHub CI、builtin Workflow、Workflow DSL、Action 或 Runner。
