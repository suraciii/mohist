# Contributing to mohist

感谢你对 mohist 的关注！

## 开发环境

### 前置条件

- Node.js >= 18.0.0
- npm >= 9.0.0
- opencode CLI

### 安装

```bash
# 克隆仓库
git clone https://github.com/owner/mohist.git
cd mohist

# 工作目录
cd packages/cli

# 安装依赖
npm install

# 构建（含 backend + web UI）
npm run build
```

### 开发流程

1. **创建分支**
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **修改代码**
   - 遵循已有代码风格
   - 为新功能编写测试
   - 必要时更新文档

3. **运行测试**
   ```bash
   npm test
   npm run test:web      # Web UI 测试
   ```

4. **代码检查**
   ```bash
   npm run lint
   ```

5. **类型检查**
   ```bash
   npm run typecheck
   ```

6. **提交**
   ```bash
   git add .
   git commit -m "feat: add your feature"
   ```

7. **推送并创建 PR**
   ```bash
   git push origin feature/your-feature-name
   ```

## 项目结构

```
packages/cli/
├── bin/                        # CLI 入口
│   ├── mo                      # 主 CLI
│   └── mo-server               # 服务入口
├── src/
│   ├── agent-runtime/          # Agent 运行时管理
│   ├── agent-skills/           # Skill 调度
│   ├── agents/                 # Agent 提示词和配置
│   │   └── prompts/            # 阶段 prompt (plan/build/check/explore 等)
│   │       └── artifacts/      # 产物模板
│   ├── api/                    # HTTP API 路由
│   ├── artifacts/              # 产物读写
│   ├── cli/                    # CLI 命令实现
│   ├── config/                 # 配置管理
│   ├── db/                     # SQLite 数据层
│   ├── git/                    # Git 操作 (worktree/diff/merge)
│   ├── openspec/               # OpenSpec 集成
│   ├── project/                # 项目管理
│   ├── server/                 # HTTP Server + 状态管理
│   ├── services/               # 业务逻辑层
│   ├── tools/                  # 工具函数
│   ├── types/                  # TypeScript 类型定义
│   ├── util/                   # 通用工具
│   ├── utils/                  # 工具函数
│   └── workflow/               # 工作流引擎 (Plan/Build/Check/Integrate runners)
├── web/                        # Web UI (React + Vite + Tailwind)
│   ├── src/
│   │   ├── components/         # 页面组件
│   │   ├── hooks/              # React hooks (含 useSSE)
│   │   ├── lib/                # API 客户端、类型、工具
│   │   └── context/            # React context (ProjectContext 等)
│   └── package.json
├── tests/                      # 后端测试
├── package.json
├── tsconfig.json
└── vitest.config.ts
```

## 代码风格

### TypeScript

- 使用 strict mode
- 优先使用 interface 描述对象形状
- 使用 enum 描述固定值集合
- 公共 API 添加 JSDoc 注释

### 通用

- 使用有意义的变量名
- 保持函数小而专注
- 优先组合而非继承
- 编写自文档化代码

## 测试

### 单元测试

测试文件放在 `tests/` 目录或源文件旁：

```typescript
import { describe, it, expect } from 'vitest';

describe('MyComponent', () => {
  it('should do something', () => {
    // test code
  });
});
```

### Web UI 测试

```bash
npm run test:web
```

### 阶段状态专项测试

```bash
npm run test:stage-state
```

## Commit 规范

遵循 [Conventional Commits](https://www.conventionalcommits.org/)：

- `feat:` 新功能
- `fix:` Bug 修复
- `docs:` 文档变更
- `style:` 代码风格（格式化等）
- `refactor:` 代码重构
- `test:` 测试相关
- `chore:` 构建或工具链变更

示例：
```
feat: add pause/resume functionality for issues

- Implement pause command
- Add tests for pause/resume
- Update documentation
```

## PR 规范

1. **标题**: 使用 conventional commit 格式
2. **描述**: 解释做了什么、为什么，而非怎么做
3. **测试**: 包含新功能的测试
4. **文档**: 更新相关文档
5. **Breaking Changes**: 标注破坏性变更

## Debug

### 服务器日志

```bash
# CLI 查看
mo server logs -n 100

# Web UI 实时查看
# 打开 /logs 页面，支持级别过滤和文本搜索
```

### Agent 日志

Agent 输出捕获在服务端日志中。

### Debug 模式

```bash
# 设置日志级别
mo config logLevel DEBUG
```

## License

贡献的代码将采用 MIT License。