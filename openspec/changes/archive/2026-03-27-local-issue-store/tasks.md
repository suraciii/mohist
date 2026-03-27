## 1. 数据库扩展

- [x] 1.1 扩展 migrations.ts，添加 schema version 2
- [x] 1.2 在 issues 表添加 `labels TEXT DEFAULT '[]'` 列
- [x] 1.3 创建 comments 表（id, issue_id, body, created_at）
- [x] 1.4 添加 comments 表索引

## 2. 数据层扩展

- [x] 2.1 扩展 Issue 类型，添加 `labels: string[]` 字段
- [x] 2.2 扩展 IssueRepo，支持 labels 的读取和更新
- [x] 2.3 实现 CommentRepo（create, findByIssue, deleteByIssue）
- [x] 2.4 实现 LabelRepo（findAllUsed - 聚合查询所有使用过的 labels）

## 3. Server API 扩展

- [x] 3.1 实现 POST /api/issues（创建 Issue，支持 labels）
- [x] 3.2 实现 PATCH /api/issues/:number（更新 Issue，支持 labels 操作）
- [x] 3.3 实现 POST /api/issues/:number/comments（添加评论）
- [x] 3.4 实现 GET /api/labels（列出所有 labels）
- [x] 3.5 扩展 GET /api/issues，支持 labels 过滤

## 4. CLI 命令扩展

- [x] 4.1 实现 `ph issue create "title" [-l label]...` 命令
- [x] 4.2 实现 `ph issue update <id>` 命令
  - [x] 4.2.1 `--title` flag
  - [x] 4.2.2 `--body` flag
  - [x] 4.2.3 `-l +label` / `-l -label` flag
- [x] 4.3 实现 `ph issue close <id>` 命令
- [x] 4.4 实现 `ph issue reopen <id>` 命令
- [x] 4.5 实现 `ph issue comment <id> "text"` 命令
- [x] 4.6 实现 `ph label list` 命令

## 5. 输出格式化

- [x] 5.1 实现 `project#number` 显示格式
- [x] 5.2 扩展 issue list 输出，显示 labels
- [x] 5.3 实现 issue show 输出，包含 comments

## 6. 测试

- [x] 6.1 为 IssueRepo labels 操作添加单元测试
- [x] 6.2 为 CommentRepo 添加单元测试
- [x] 6.3 为 LabelRepo 添加单元测试
- [x] 6.4 为新增 API 端点添加集成测试
- [x] 6.5 为新增 CLI 命令添加测试

## 7. 文档

- [x] 7.1 更新 README.md，添加新命令使用示例
- [x] 7.2 更新 API 文档（如有）
