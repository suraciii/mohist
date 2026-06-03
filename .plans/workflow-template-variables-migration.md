# Workflow Template & Variables 改造计划

> 基于 `design/workflow-template-variables.md` 的设计，将现有变量解析架构重构为 5 层分离模型。

## 设计目标

```text
现状:
  - 变量混合存储在 WorkflowExecutionContext.Json (opaque string)
  - 模板与变量耦合在 IssueWorkflowProfile 类
  - 4 层隐式合并由 WorkflowExecutionContext.ToDispatchJson() 完成

目标:
  - 变量分 5 层独立存储: template-embedded + project + issue + workflow-run + dispatch-injection
  - Template 与 Variables 类型分离, 字段统一命名 (Template + Variables)
  - WorkflowProfileManager 作为唯一读入口
  - VariableBundle 作为三层独立变量统一类型
```

## 关键设计契约 (来自 design/*.md)

```text
VariableBundle:
  {
    vars:   { ... },            <- 全局变量
    stages: {
      "plan":  { vars: {...} },
      "build": { vars: {...} }
    }
  }

5 层合并优先级 (低 -> 高):
  1. template.embedded          (YAML `variables:` 段)
  2. project.vars               (project_workflow_profile.Variables)
  3. issue.vars                 (issue_workflow_profile.Variables)
  4. workflow-run.vars          (workflow_profile.Variables)
  5. dispatch injection         (workflow.runId, stage.name, work.*)

数据表命名 (字段命名规范):
  project_workflow_profile   key: projectId
    DefaultTemplateId, Variables
  project_templates          ProjectId, TemplateId, Template (JSON)
  issue_workflow_profile     SourceTemplateId, Template (JSON), Variables
  workflow_profile           RunId, Template (JSON), Variables, ProjectId, IssueId

API:
  Set (PUT) -> 完整替换
  Patch (PATCH) -> deep merge
```

---

## 执行步骤 (13 步)

### Step 1: VariableBundle 核心类型 [本次会话]
- 新建: `Workflow/Domain/VariableBundle.cs`
- 新建: `Workflow/Domain/VariableBundleMerge.cs`
- 新建测试: `tests/VariableBundleSpecs.cs`
- 验证: 编译 + 全部测试通过

### Step 2: DB Schema (新表) [本次会话]
- 新建 EF Core migration
- 新增 4 个 Row 类型 (ProjectWorkflowProfileRow, ProjectTemplateRow, IssueWorkflowProfileRow, WorkflowProfileRow)
- 注册 DbSet
- 保留旧表 (向后兼容)
- 验证: `dotnet ef database update` 成功

### Step 3: WorkflowProfileManager (读入口) [本次会话]
- 新建: `Workflow/Infrastructure/WorkflowProfileManager.cs`
- 新建: `Workflow/Infrastructure/ResolvedTemplate.cs`
- 新建测试: `tests/WorkflowProfileManagerSpecs.cs`
- 验证: 单元测试覆盖 5 层合并优先级

### Step 4: ProjectWorkflowProfileManager (项目写端) [后续会话]
- 新建: `Workflow/Infrastructure/ProjectWorkflowProfileManager.cs`
- 实现 template CRUD + variables Set/Patch

### Step 5: IssueWorkflowProfileManager (issue 写端) [后续会话]
- 新建: `Workflow/Infrastructure/IssueWorkflowProfileManager.cs`
- 实现 UpdateTemplate + Set/Patch Variables

### Step 6: DI Registration [后续会话]
- 注册 3 个新 Manager
- 删除 WorkflowVariableResolver 注册

### Step 7: WorkflowGrain Dispatch 切换 [后续会话]
- 替换 MakeDispatchAsync 中的变量解析逻辑
- 删除 ApplyStageAgentDefault / DeepMergeAgentObject
- 关键风险点 - 需要充分测试

### Step 8: IssueGrain 启动流程 [后续会话]
- 改为调 IssueWorkflowProfileManager
- 简化 WorkflowGrain.StartAsync 参数

### Step 9: API 层改造 [后续会话]
- 新增 system template catalog
- 新增 project template CRUD
- 修改 variables 端点
- 替换 issue/workflow-run variables

### Step 10: Runner 侧确认 [后续会话]
- 验证 runner 接收 work.with 行为不变

### Step 11: 数据迁移工具 [可选 - 后续会话]
- 旧表到新表的数据同步脚本

### Step 12: 删除旧代码 [后续会话]
- 删除 WorkflowVariableResolver, WorkflowExecutionContext 等
- 删除 IssueWorkflowProfile 系列类
- 删除 ProjectVariablesBag

### Step 13: 删除旧表 [后续会话]
- 生成删除旧表/列的 EF migration

---

## 本次会话目标

完成 Step 1 + Step 2 + Step 3 的基础设施, 后续会话继续 Step 4-13。

## 验证检查点

每个 Step 完成后:
1. `dotnet build` 成功
2. `dotnet test` 全部通过
3. 新增的单元测试通过
4. 现有测试无回归

## 回滚方案

- Step 12 之前, 旧代码全部保留
- 任何 Step 出问题可暂时保留旧 dispatch 路径, 新代码不激活

---

## 进度跟踪

- [x] Step 1: VariableBundle 核心类型 ✅ (17/17 VariableBundleSpecs 通过)
- [x] Step 2: DB Schema (新表) ✅ (4 新表 + EF migration, 3/3 DatabaseInitializationSpecs 通过)
- [x] Step 3: WorkflowProfileManager ✅ (13/13 WorkflowProfileManagerSpecs 通过)
- [x] Step 4: ProjectWorkflowProfileManager ✅ (17/17 ProjectWorkflowProfileManagerSpecs 通过)
- [x] Step 5: IssueWorkflowProfileManager ✅ (10/10 IssueWorkflowProfileManagerSpecs 通过)
- [x] Step 6: DI Registration ✅ (WorkflowProfileManager + Project + Issue 都已注册)
- [ ] Step 7: WorkflowGrain Dispatch 切换 (关键切换点)
- [ ] Step 8: IssueGrain 启动流程改造
- [ ] Step 9: API 层改造
- [ ] Step 10: Runner 侧确认
- [ ] Step 11: 数据迁移工具 (可选)
- [ ] Step 12: 删除旧代码
- [ ] Step 13: 删除旧表
