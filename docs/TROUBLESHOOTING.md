# Troubleshooting Guide

Mohist 常见问题和解决方案。

## 更新后快速检查

**场景**: 执行 `mo update` 后，确认本地 server 和 runner 已就绪

**检查步骤**:
1. 确认 server systemd 服务运行中: `mo server status`
2. 确认 server HTTP 健康状态正常: `mo server health`
3. 确认 runner systemd 服务运行中: `mo runner status`
4. 再查看 runner 日志，确认 runner 已启动、正在连接或已连接 server，且没有持续报错: `mo runner logs`
5. 打开 Web UI `http://localhost:3456`，确认页面正常加载
6. 如果当前项目里已经有 issue，任选一条查看日志，确认该 issue 的 workflow 已开始产生日志: `mo issue logs <number>`
7. 运行 `git diff --check`，确认当前工作区没有 whitespace 或 conflict marker 问题；它也是 workflow 健康门控的一部分，但不表示编译、测试或 typecheck 已通过

如果以上检查均通过，即可开始或恢复 workflow 工作。如有异常，参考下方对应阶段的排查步骤。

## Plan 阶段

### Plan 产物缺失

**现象**: Plan 阶段报错缺少 proposal.md / specs / design.md / tasks.json

**原因**: 外部 coder agent 未成功生成产物

**解决**:
1. 检查 `openspec/changes/{slug}/` 目录
2. 查看 issue 日志: `mo issue logs <number>`
3. Web UI 中查看 Issue 详情页 Workflow 视图
4. 重新启动: 外部 coder agent 会尝试自动修复（1 次）

### Self-review 未通过

**现象**: Plan 完成后 self-review 标记失败

**原因**: 外部 coder agent 自审发现 plan 产物存在问题，且自动修复未能解决

**解决**:
1. 检查 `self-review.md` 中的问题列表
2. 手动修复 proposal.md / design.md / specs/ 中的问题
3. 使用 `mo issue resume <number> --skip-to-review` 跳过审核直接进入 Build

### tasks.json 未生成

**现象**: Plan 完成但 `tasks.json` 不存在

**原因**: 自审可能静默失败

**解决**:
1. 确认 `specs/` 目录有内容
2. 检查每个 spec 有完整的 GIVEN/WHEN/THEN
3. 确认 REQ-XXX 引用有效
4. 手动创建 tasks.json 或重新启动 issue

## Build 阶段

### 任务失败

**现象**: 某个 task 执行失败

**原因**: 代码实现无法满足验收标准，或环境问题

**解决**:
1. 外部 coder agent 自动重试（默认 2 次）
2. 每次重试包含失败上下文
3. 如持续失败，检查:
   - `tasks.json` 中的验收标准
   - `specs/{capability}/spec.md` 中的规格要求
4. 手动验证实现是否正确
5. 如验收标准有问题，更新后重新运行

### 健康检查失败

**现象**: Build 阶段完成后 `git diff --check` 失败

**原因**: 生成的变更存在 whitespace 或 conflict marker 问题，因此未通过该健康门控

**解决**:
1. 外部 coder agent 自动修复（默认 2 次重试）
2. 如果自动修复无效，进入 Plan 被驳回流程
3. 查看 issue 日志定位具体错误
4. 在 Web UI 中查看 Changes 面板的 diff 定位问题代码

### Build 卡住

**现象**: 任务长时间无进展

**原因**: 可能是 timeout 或 外部 coder agent 等待输入

**解决**:
1. 查看 coder agent 日志: `mo server logs`
2. 查看 Web UI `/activity` 页面
3. 如果 timeout，task 可能需要拆分
4. 强制停止: `mo issue close <number>` 或 Web UI 中 Force Stop
5. 调查原因后重新打开: `mo issue reopen <number>`

### 零工作保护触发

**现象**: Build 阶段报错 "total tasks > 0 but completed = 0"

**原因**: `tasks.json` 中有任务但全部未执行

**解决**:
1. 检查 `tasks.json` 任务列表是否正确
2. 确认前置步骤是否完成
3. 重新启动 issue

## Check 阶段

### 审查未通过

**现象**: `review-passed` 检查失败

**原因**: 外部 coder agent 审查发现代码问题

**解决**:
1. 外部 coder agent 自动修复（默认 3 次重试）
2. 查看 Web UI 中的 `FullReportModal` 了解具体问题
3. 每次重试前删除过期的 `review.md` 并重新审查
4. 手动修复后重新运行

### 合并冲突

**现象**: `merge-ready` 检查失败

**原因**: 目标分支有新提交，worktree 无法快进合并

**解决**:
1. 外部 coder agent 自动 rebase（默认 2 次重试）
2. 查看 Web UI 中的 `MergeStatePanel` 了解冲突详情
3. Rebase 冲突实时追踪通过 SSE 推送
4. 手动 rebase 或重试合并: Web UI 中 "Retry Merge"

## Integrate 阶段

### Spec sync 失败

**现象**: 增量规格无法同步到主规格

**原因**: `specs/` 文件格式问题或主规格冲突

**解决**:
1. 检查 `openspec/changes/{slug}/specs/` 目录
2. 确认 spec 文件格式正确
3. 手动运行规格同步命令

### Merge 失败

**现象**: 压缩合并到目标分支失败

**原因**: 可能是权限、分支保护或冲突

**解决**:
1. 查看 `mo issue show <number>` 中的 merge 状态
2. Web UI 中查看 `MergeStatePanel`
3. `mo issue retry-merge <number>` 重试
4. 手动处理合并冲突后继续

### 集成后健康检查失败

**现象**: `git diff --check` 失败

**原因**: 合并后的变更存在 whitespace 或 conflict marker 问题

**解决**:
1. Integrate 阶段不自动修复此失败——这是最后防线
2. 检查合并后的代码状态
3. 手动修复后重新触发集成

## Web UI 问题

### 页面白屏

**现象**: 打开 Web UI 后页面空白

**解决**:
1. 确认 server 运行: `mo server status`
2. 检查浏览器 console（F12）
3. 确认端口 3456 未被占用
4. 重建 Web UI: `npm run build:web`

### 数据不刷新

**现象**: 页面显示旧数据

**原因**: SSE 连接可能断开

**解决**:
1. 刷新页面重新建立 SSE 连接
2. 检查浏览器是否支持 EventSource
3. 检查网络连接（SSE 每 30 秒有心跳）

### Project 列表为空

**现象**: Web UI 中没有项目可切换

**解决**:
1. 确认已初始化项目: `mo init`
2. 确认 project 已创建: `mo project list`
3. 检查 server 是否正常: `mo server status`

## Coder Agent 问题

### Coder agent provider authentication failed

**现象**: External coder agent reports provider authentication or configuration failure

**解决**:
1. Check the Mohist model catalog: `mo providers list`
2. Configure provider credentials in the external coder agent, such as opencode
3. Web UI 中 Settings → Coder Agent 查看 model catalog
4. Mohist does not test provider connectivity; test it in the external coder agent

### Coder agent model not selected

**现象**: 探索页面无法发送消息

**原因**: No default coder agent model is selected

**解决**:
1. Web UI 中 Settings → Coder Agent → Default Coder Agent Model 选择模型
2. or `mo config set model anthropic/claude-sonnet-4-20250514`

## 配置问题

### 配置不生效

**现象**: 修改配置后未看到效果

**解决**:
1. 确认配置路径: `~/.mohist/config.jsonc`
2. 使用 `mo config --list` 验证当前值
3. 部分配置需要重启 server 生效
4. Coder agent runtime configuration可在 Web UI Settings 中实时修改

## 命令参考 (常用恢复操作)

```bash
# 查看 Issue 状态
mo issue show <number>

# 重新启动 workflow
mo issue reopen <number>

# 跳过 Plan review 进入 Build
mo issue resume <number> --skip-to-review

# 强制重新创建 Change
mo propose <number> --force

# 重试合并
mo issue retry-merge <number>

# 查看代码差异
mo issue diff <number>

# 实时日志
mo issue logs <number> -f

# 查看全局事件
mo attach -f
```

## 获取帮助

1. 查看日志: `mo server logs` 或 Web UI `/logs`
2. 查看 OpenSpec 产物: `openspec/changes/{slug}/`
3. 检查工作区 whitespace 或 conflict marker 问题: `git diff --check`
4. 运行测试: `dotnet test Mohist.sln`
5. 提交 Issue: https://github.com/owner/mohist/issues
