## 完善后的方案总结

### 1. 新增任务 T-000：快速修复 propose.ts（最高优先级）

**问题**：`propose.ts:65` 调用 `worktreeManager.create()` 时未传入 `baseBranch`，使用默认 `'main'`。

**修复**：一行代码修改，零风险，立即生效。

```typescript
// 修改前 (line 65)
worktreePath = await worktreeManager.create(project.path, project.name, issue.number);

// 修改后
worktreePath = await worktreeManager.create(project.path, project.name, issue.number, project.baseBranch);
```

**理由**：
- 这是导致当前 bug 的直接原因之一
- 零风险修复，不需要改动其他逻辑
- 可以立即部署，缓解问题

---

### 2. 调整后的任务优先级

| 优先级 | 任务 | 说明 |
|--------|------|------|
| **P0** | T-000 | propose.ts 一行修复（立即生效） |
| **P1** | T-001 | detectBaseBranch 多级探测 |
| **P2** | T-002 | worktree-manager prune + merge-base 验证 |
| **P3** | T-003 | CLI diff 使用 API baseBranch |
| **P4** | T-004 | （已合并到 T-000） |
| **P5** | T-005 | 集成验证 |

---

### 3. T-001 补充：Migration 中的同步版本也需更新

**新增文件修改**：`src/db/migrations.ts:285-308`

`detectBaseBranchSync()` 函数也需要改为多级探测，否则 migration 时会写入错误的 base_branch。

**探测顺序**：
```
origin/HEAD → origin/main → origin/master → HEAD分支 → 'main'
```

**实现方式**：使用 `execFileSync` 依次尝试每个探测点。

---

### 4. T-003 补充：API 响应需包含 baseBranch

**问题**：CLI diff 命令需要从 API 获取 `baseBranch`，但当前 issue detail API 响应不包含此字段。

**解决方案**：

当前 API 响应（`issues.ts:148-155`）：
```typescript
data: {
  ...issue,
  projectName: project?.name || 'unknown',
  projectPath: project?.path || '',
  comments
}
```

**需要添加**：`baseBranch: project?.baseBranch || 'main'`

**CLI diff 命令修改**（`issue.ts:420-447`）：
```typescript
// 修改前
const defaultBranch = getDefaultBranch(projectPath);

// 修改后
const baseBranch = issue.baseBranch || 'main'; // 从 API 响应获取
```

---

### 5. 遗留的硬编码 'main'

以下位置的硬编码 `'main'` 不需要修改（理由已在设计文档中说明）：

| 文件 | 位置 | 理由 |
|------|------|------|
| `project-repo.ts:33` | `create()` 默认值 | 会被 `detectBaseBranch` 覆盖 |
| `project/detector.ts:34` | 配置回退 | 检测失败时的合理默认值 |
| `project/manager.ts:21` | 内存存储默认值 | 测试/开发用途，非生产代码 |

---

### 6. 更新后的 prd.json

```json
{
  "project": "mohist",
  "description": "Fix worktree base branch resolution: multi-level detection, unified consumption, stale ref pruning, and merge base validation",
  "tasks": [
    {
      "id": "T-000",
      "title": "Quick fix: propose.ts pass baseBranch to worktreeManager.create()",
      "spec": "specs/base-branch-resolution/spec.md",
      "description": "In src/api/propose.ts line 65, pass project.baseBranch as the 4th argument to worktreeManager.create(). This is a zero-risk one-line fix that resolves the immediate issue.",
      "acceptanceCriteria": [
        "propose.ts passes project.baseBranch to worktreeManager.create()",
        "Typecheck passes"
      ],
      "priority": 0,
      "passes": false,
      "notes": "Immediate fix. See design decision D5."
    },
    {
      "id": "T-001",
      "title": "Rewrite detectBaseBranch with multi-level probe (async and sync versions)",
      "spec": "specs/base-branch-resolution/spec.md",
      "description": "Rewrite detectBaseBranch() in src/git/detect-base-branch.ts and detectBaseBranchSync() in src/db/migrations.ts to use multi-level probe: origin/HEAD → origin/main → origin/master → HEAD branch → 'main'. Each level checks existence before returning.",
      "acceptanceCriteria": [
        "detectBaseBranch returns 'master' when origin/HEAD points to master",
        "detectBaseBranch returns 'main' when origin/HEAD fails but origin/main exists",
        "detectBaseBranch returns 'master' when origin/HEAD and origin/main fail but origin/master exists",
        "detectBaseBranch returns current HEAD branch when all remote probes fail and HEAD is not detached",
        "detectBaseBranch returns 'main' when project is not a git repo",
        "detectBaseBranchSync (in migrations) has same multi-level logic",
        "Typecheck passes"
      ],
      "priority": 1,
      "passes": false,
      "notes": "See design decision D1. Both async and sync versions must be updated."
    },
    {
      "id": "T-002",
      "title": "Add --prune to smartFetch and merge-base validation after worktree creation",
      "spec": "specs/worktree-manager/spec.md",
      "description": "In src/git/worktree-manager.ts: (1) Change smartFetch to use 'git fetch origin --prune' instead of 'git fetch origin'. (2) After worktree creation, run git merge-base to verify the new branch shares history with base branch. If no merge base, auto-remove the worktree and branch, then throw error.",
      "acceptanceCriteria": [
        "smartFetch runs 'git fetch origin --prune'",
        "Worktree creation succeeds when merge base exists with base branch",
        "Worktree creation fails with descriptive error when no merge base exists",
        "Failed worktree creation auto-cleans the created worktree and branch",
        "Typecheck passes"
      ],
      "priority": 2,
      "passes": false,
      "notes": "See design decisions D3 and D4."
    },
    {
      "id": "T-003",
      "title": "Fix CLI diff to use project baseBranch from API and add baseBranch to issue detail response",
      "spec": "specs/base-branch-resolution/spec.md",
      "description": "(1) In src/api/issues.ts issue detail endpoint, add baseBranch to the response. (2) In src/cli/commands/issue.ts diff command: remove getDefaultBranch() function, use baseBranch from API response instead.",
      "acceptanceCriteria": [
        "Issue detail API response includes baseBranch field",
        "mo issue diff uses baseBranch from API response",
        "getDefaultBranch() function is removed from issue.ts",
        "Typecheck passes"
      ],
      "priority": 3,
      "passes": false,
      "notes": "See design decision D2."
    },
    {
      "id": "T-004",
      "title": "Verify full pipeline with correct base branch",
      "spec": "specs/worktree-manager/spec.md",
      "description": "Build the project and verify: (1) npm run build succeeds, (2) mo server starts, (3) create a test issue, start it, verify worktree is created from correct base branch (check merge-base with master), (4) mo issue diff works without 'no merge base' error.",
      "acceptanceCriteria": [
        "npm run build succeeds",
        "Worktree for new issue has common ancestor with project base branch",
        "mo issue diff shows output without 'no merge base' error",
        "Typecheck passes"
      ],
      "priority": 4,
      "passes": false,
      "notes": "Integration verification task. If DB has stale base_branch='main', update it via API to 'master' first."
    }
  ]
}
```

---

### 7. 关键决策确认

**Q: 为什么 T-000（propose.ts 修复）要单独提出？**

A: 
1. **风险最低**：一行代码修改，只改函数调用参数
2. **立即见效**：不需要等其他任务完成，可以马上部署
3. **独立性强**：不依赖 detectBaseBranch 的重写

**Q: 为什么 migration 中的同步版本也需要改？**

A: 
- Migration 会在用户升级时自动运行
- 如果 detectBaseBranchSync 还是硬编码 `'main'`，会把错误的值写入 DB
- 导致升级后新项目仍然有问题

**Q: CLI diff 为什么不直接调用 API 获取 project？**

A:
- 当前实现已经调用了 issue detail API
- 只需要在 API 响应中添加 `baseBranch` 字段即可
- 避免额外 API 调用，保持简单

---

### 8. 实施建议

1. **立即执行 T-000**：合并到 main，缓解当前问题
2. **并行开发 T-001 和 T-002**：这两个任务相互独立
3. **最后执行 T-003 和 T-004**：依赖前面的改动
4. **部署前检查**：如果现有项目 DB 中有 `base_branch='main'` 但实际是 `master`，需要手动更新或重新注册项目

这个完善后的方案解决了所有发现的问题，并优化了任务优先级。需要我创建更新后的 OpenSpec 变更提案吗？