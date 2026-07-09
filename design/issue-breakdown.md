---
status: deferred
---

# Issue Breakdown / Sub-issue

> 原 backlog issue #96 已关闭。#281 明确将自动 breakdown 列为 Non-Goal。本文件记录产品方案的开放问题。

## 背景

原 #96 提议三段式方案：Agent 分析 parent issue → 生成 `issue-breakdown.json` artifact → 审批 → 创建 sub issues + 持久化 `IssueLink(type=child)`。

## 为什么暂不实现

1. **与 Epic 重叠**：Epic 已是 issue 的组织层（`LinkIssueAsync`、`2/3 done` 聚合进度、自动完成父项），`IssueLink(type=child)` 会形成第二套平行的 parent→child 关系。

2. **#281 明确拒绝**：`openspec/changes/archive/2026-06-29-issue-281/design.md` Non-Goal："No automatic issue breakdown or batch child-issue creation."

3. **通用 IssueLink 过度设计**：`blocks`/`relates` 类型纯属投机——prerequisites 已覆盖 start-ordering；7 个 provenance 字段 + 3 个新 action + 2 个 artifact schema 违反简洁原则。

4. **artifact 消费链 gap**：runner 没有"跨审批边界按 id 读取已存储 artifact 注入后续 action"的机制。

## 唯一成立的前提

Sub-issue 与 prerequisite 正交：prerequisite 表达 start-ordering（B 不能在 A 完成前开始），sub-issue 表达 origin（B 从 A 拆出）。这两个轴确实不同。

## 后续需想清的问题

1. **Epic vs Sub-issue 边界**：如果"拆大 issue"就是"建 Epic + 成员 issue"，sub-issue 作为独立概念是否必要？
2. **最小可行版本**：Agent 生成 breakdown 文档（markdown），用户手动创建 issue 挂到 Epic？
3. **跨审批边界的 artifact 消费**：runner 需要先支持读取已存储 artifact 注入后续 action。
4. **与 #94 batch-link 的协同**：如果拆分折叠进 Epic，batch-link 就是落地工具。
