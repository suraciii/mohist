# Contributing to mohist

感谢你对 mohist 的关注！

## 开发环境

### 前置条件

- Node.js >= 22.19.0 (the repository pins 22.19.0 in `.nvmrc`)
- npm >= 10.0.0
- opencode CLI

### 安装

```bash
# 克隆仓库
git clone https://github.com/owner/mohist.git
cd mohist

# 安装依赖
npm install

# 构建（含 Web UI + .NET Server）
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
   npm --prefix packages/web run test:run      # Web UI 测试
   ```

4. **提交**
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
packages/server/
├── src/Mohist.Server/          # ASP.NET Core + Orleans backend
└── tests/Mohist.Server.Tests/  # 后端 spec/集成测试

packages/runner/
├── src/                        # TypeScript runner runtime
└── package.json                # standalone runner package

packages/web/
├── src/                        # React Web UI
└── tests/                      # Web UI tests
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
npm --prefix packages/web run test:run
```

### 阶段状态专项测试

测试运行在 Microsoft Testing Platform (MTP) 下，过滤语法与 VSTest 不同——通过 `--` 透传 MTP 原生选项：

```bash
dotnet test Mohist.sln -- --filter-class "*Stage*"
```

其它常用 MTP 过滤选项：`--filter-method`、`--filter-fully-qualified-name`、`--filter-test-node-uids`。

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

## 文档贡献

改文档前，先读 [`README.md`](./README.md) 的"写作约束"。

**动手前**：重读该篇文末"对应源码"指向的代码，确认要改的事实陈述没因产品变更而过时。

**提 review 前自查**：

- [ ] 每条事实陈述都能在源码里找到对应
- [ ] 代码里没有的功能不写（即使 UI / 数据库字段 / handler 接口有暗示）
- [ ] 所有 shell / CLI 示例可以独立复制运行
- [ ] 术语和 [`README.md`](./README.md) 一致
- [ ] 所有链接指向的页面存在（没有死链）

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
# 通过服务端配置调整日志级别；细节见 Mohist 服务端配置文档。
```

## License

贡献的代码将采用 MIT License。
