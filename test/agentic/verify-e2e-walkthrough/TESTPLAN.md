# Test Plan: E2E Walkthrough

## Context

在容器隔离环境中走完整 mohist 工作流。

**环境**: 容器中 server 已启动 (localhost:3456)，工作目录 /app/workspace。
**数据目录**: /home/motest/.mohist/
**范围**: 完整 pipeline — server health → project → issue → design → implement → review → done。
**前置条件**: 基础镜像 (Layer A) 不含 opencode。完整 pipeline 需要 Layer B 镜像。如果只有 Layer A，pipeline 会在 agent spawn 阶段 blocked。

---

## Phase 1: Server Health

请求 GET /api/health，验证返回 JSON 中 `status` 为 `"ok"`。

```bash
curl -sf http://localhost:3456/api/health
```

---

## Phase 2: Environment Setup

配置 git identity（容器内无默认配置）：

```bash
git config --global user.email "motest@test.local"
git config --global user.name "motest"
```

---

## Phase 3: Project Setup

使用辅助脚本创建 project（脚本内含 git init + initial commit + mo project create）：

```bash
@scripts/create-project.sh walkthrough-test
mo project list  # 确认 walkthrough-test 存在
```

---

## Phase 4: Create Issue

```bash
mo issue create "E2E walkthrough test" --body "验证完整工作流的端到端测试"
mo issue show 1  # stage=draft, status=active
```

---

## Phase 5: Start Issue (Design Phase)

```bash
mo issue start 1
```

进入监控循环：
- 每 30s 执行 `@scripts/check-status.sh 1`
- 关注 stage 变化: plan → designing → waiting-design-review
- 关注 agent 进程是否存活
- 如果 agent 消失且 stage 未变化，标记异常

**超时**: 10 分钟内未到达 waiting-design-review → 标记失败并分析。

---

## Phase 6: Approve Design

1. 查看设计产物：`mo issue show 1`
2. 检查 worktree 中 openspec 目录是否生成
3. 执行审批
4. 继续监控 implementing 阶段

**超时**: 审批后 15 分钟内应到达 waiting-review。

---

## Phase 7: Approve Implementation

1. 等待到达 waiting-review
2. 查看实现产物（worktree 中代码变更）
3. 执行审批

---

## Phase 8: Verify Done

1. `mo issue show 1` → stage=done
2. 检查产物完整性
3. `mo issue list` → 确认 issue 最终状态

---

## Phase 9: Collect Results

```bash
@scripts/collect-logs.sh
```

汇总所有 Phase 的通过/失败状态。
