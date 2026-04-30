## ADDED Requirements

### Requirement: workflow.yaml 支持 checks 配置

系统 SHALL 支持 workflow.yaml 中的 `checks` 配置段，控制 Check stage 的检查行为。

```yaml
checks:
  build-test:
    command: 'npm run build && npm test'
    timeout: 300000
    autoFix: true
    maxFixAttempts: 2
  ff-merge:
    enabled: true
  ai-review:
    enabled: true
```

#### Scenario: 读取默认 checks 配置
- **WHEN** 项目没有 `workflow.yaml` 或 workflow.yaml 不包含 `checks` 段
- **THEN** 系统使用默认 checks 配置：
  - `build-test.command: 'npm run build && npm test'`
  - `build-test.timeout: 300000`（5 分钟）
  - `build-test.autoFix: true`
  - `build-test.maxFixAttempts: 2`
  - `ff-merge.enabled: true`
  - `ai-review.enabled: true`

#### Scenario: 自定义 build-test 命令
- **WHEN** workflow.yaml 包含 `checks.build-test.command: 'cargo test'`
- **THEN** Build & Test check 运行 `cargo test` 而非默认的 `npm run build && npm test`

#### Scenario: 禁用 autoFix
- **WHEN** workflow.yaml 包含 `checks.build-test.autoFix: false`
- **THEN** Build & Test check 失败时不会尝试自动修复
- **AND** 直接报告失败

#### Scenario: 禁用 ff-merge 检查
- **WHEN** workflow.yaml 包含 `checks.ff-merge.enabled: false`
- **THEN** Merge Ready 检查被跳过
- **AND** CheckSuiteOutput 中不包含 merge-ready 的 CheckResult

#### Scenario: 禁用 AI review
- **WHEN** workflow.yaml 包含 `checks.ai-review.enabled: false`
- **THEN** AI Code Review 检查被跳过
- **AND** Check 套件在 Build & Test（和可选的 Merge Ready）通过后直接进入审批门

#### Scenario: 自定义 build-test 超时
- **WHEN** workflow.yaml 包含 `checks.build-test.timeout: 600000`
- **THEN** Build & Test check 在 10 分钟后超时

## MODIFIED Requirements

### Requirement: workflow.yaml 定义工作流阶段和 prompt 模板
系统 SHALL 支持项目根目录或 `.mohist/` 目录下的 `workflow.yaml` 文件，声明式定义工作流阶段、每个阶段的 prompt 模板、以及 Check stage 的检查配置。

#### Scenario: 读取默认 workflow
- **WHEN** 项目没有 `workflow.yaml` 文件
- **THEN** `read_workflow` tool 返回内置默认 workflow（plan → build → check → done，每阶段包含默认 prompt，checks 使用默认配置）

#### Scenario: 读取自定义 workflow
- **WHEN** 项目根目录存在 `workflow.yaml`，内容包含 stages、prompt 和 checks 字段
- **THEN** `read_workflow` tool 返回该文件内容，Main Agent 按此编排

#### Scenario: workflow 文件格式错误
- **WHEN** `workflow.yaml` 存在但 YAML 解析失败
- **THEN** 返回错误信息，建议使用默认 workflow
