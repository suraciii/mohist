# crawlph 测试指南 (Agent 自用)

> 给 agent 自己的测试备忘，不是用户文档。

## 快速开始

```bash
# 1. 设置环境
export GH_TOKEN=$(gh auth token)
export CRAWLPH_DATA_DIR="$HOME/.openclaw/agents/crawlph-test/data"

# 2. 进入测试仓库
cd /mnt/c/Users/szf/repos/crawlph-test

# 3. 执行测试
timeout 180 openclaw agent --agent crawlph-test --local --message '/crawlph 1 --yes' --timeout 170
```

## 测试仓库配置

| 项目 | 值 |
|------|-----|
| 仓库 | `suraciii/crawlph-test` (私有) |
| Agent | `crawlph-test` |
| 数据目录 | `~/.openclaw/agents/crawlph-test/data/` |
| Workspace | `/mnt/c/Users/szf/repos/crawlph-test` |

## 验证清单

执行后检查这些状态：

```bash
# 1. Issue 被 claim
cat ~/.openclaw/agents/crawlph-test/data/crawlph-claims.json

# 2. Progress 文件创建
ls ~/.openclaw/agents/crawlph-test/data/progress/

# 3. Issue 状态
gh issue view 1

# 4. PR 状态
gh pr list

# 5. Git 日志
git log --oneline -5
```

## 关键检查点

每个 workflow stage 完成后应看到：

| Stage | 检查点 |
|-------|--------|
| exploration | Issue 添加评论, 标签变更 |
| refinement | Issue body 更新, 任务列表 |
| design | Draft PR 创建, 包含 specs |
| implementation | PR 包含代码实现 |
| review | PR 状态 "Ready for Review" |
| done | PR merged, Issue closed |

## 常见问题

### API Rate Limit
**症状**: `⚠️ API rate limit reached`
**原因**: GH_TOKEN 未设置，使用未认证请求
**解决**:
```bash
export GH_TOKEN=$(gh auth token)
```

### 数据目录错误
**症状**: Claims/progress 写入到 crawlph 而非 crawlph-test
**原因**: CRAWLPH_DATA_DIR 未传递
**解决**: 确保环境变量已设置

### Agent 不存在
**症状**: `Error: Agent crawlph-test not found`
**解决**: 运行 `python scripts/setup_test_agent.py` 创建（见 scripts/setup_test_agent.py）

### API Rate Limit (重要)
**症状**: `⚠️ API rate limit reached. Please try again later.`
**原因**: GH_TOKEN 未设置，使用未认证请求 (60次/小时限制)
**解决**:
```bash
export GH_TOKEN=$(gh auth token)
```
**提升**: 认证后 5000次/小时

**监控**:
```bash
watch -n 10 'gh api /rate_limit | jq .resources.core'
```

## 创建测试 Issue

```markdown
## 功能请求: 添加 Hello World 脚本

### 需求
创建一个简单的 Python 脚本，输出 "Hello, World!"。

### 验收标准
- [ ] 创建 hello.py 文件
- [ ] 脚本运行时输出 "Hello, World!"
- [ ] 包含适当的 shebang 和文档

### 技术要求
- Python 3.x
- 代码风格符合 PEP 8

Labels: stage:exploration
```

## 监控命令

```bash
# 实时监控日志
tail -f /tmp/test-run.log

# 检查 Issue 状态
gh issue view 1

# 检查 PR
gh pr list
gh pr view 2

# 查看数据文件
watch -n 5 'cat ~/.openclaw/agents/crawlph-test/data/crawlph-claims.json'
```

## 测试清理

```bash
# 关闭测试 Issue
gh issue close 1 --comment "Test completed"

# 删除 PRs
gh pr list --json number | jq -r '.[].number' | xargs -I {} gh pr close {}

# 重置 (谨慎使用)
rm -rf ~/.openclaw/agents/crawlph-test/data/progress/*
rm ~/.openclaw/agents/crawlph-test/data/crawlph-claims.json
rm ~/.openclaw/agents/crawlph-test/data/crawlph-cursor.json
```

## 备注

- 使用 `--yes` 跳过确认
- 使用 `--timeout 170` 给子代理足够时间
- 执行时间约 20-30 分钟完整 workflow
- Gateway 崩溃需手动重启
- 所有路径现在使用 `{DATA_DIR}` 占位符
