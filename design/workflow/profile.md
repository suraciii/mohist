# Workflow Profile

> profile = **template**（选哪个 `WorkflowDefinition`）+ **variables**（`VariableBundle`）。
> prompts 不属 profile → [`prompt-management.md`](../prompt-management.md)。
> action input/output 契约见 `actions.md`。内置 workflow 行为见 `builtin-workflows/`。

## 架构

```
Issue ──▶ Workflow                                      依赖方向，单向
WorkflowGrain ──▶ IWorkflowProfileProvider              端口，Workflow 定义，返回仅 Workflow 类型
                       ▲ 实现（DI 组合层）
                WorkflowProfileProvider                  adapter
                       │ live 读 config
                       ▼
        global config · project profile · issue profile · run profile · project templates
```

- Workflow 零 Issue 依赖；解析在 adapter，live 不快照。
- `WorkflowRun` 只保存执行状态和 profile 身份，不内嵌 profile variables。`WorkflowRun.RuntimeVariables` 移除。
- profile 的读取、更新、合并都走 `WorkflowProfileProvider` / profile service。
- `TaskRun.Output` 类型为 `JsonElement?`（与 `WithInput` 对标，都是 JSON object），不为 `string?`。

## VariableBundle

`{ vars, stages: { "plan": { vars } } }`。Set 整替，Patch deep merge。

## Profile Layers

### Project Profile

项目级默认配置。

```text
project_workflow_profile
  ProjectId
  DefaultTemplateId
  Variables
```

### Issue Profile

issue 级模板选择和变量覆盖。

```text
issue_workflow_profile
  IssueId
  SourceTemplateId
  Template
  Variables
```

### Run Profile

workflow run 级运行态 profile。它是最后一层变量来源，优先级最高。

```text
workflow_run_profile
  WorkflowRunId
  Variables
```

run profile 用于保存 workflow run 过程中产生、且后续 task 需要引用的运行态事实，例如：

```yaml
vars.change.id
vars.change.url
```

run profile 不属于 `WorkflowRun` aggregate state。`WorkflowRun` 通过 `WorkflowRunId` 关联它，profile service 负责读写。

## 合并

variables 合并分三段。template 先按覆盖/回退规则选出一个当前 template，
profile variables 独立合并，再由 profile variables 覆盖当前 template variables；
只有指定 stage 时才叠加 stage variables。

```text
CurrentTemplateVariables

issue custom template variables?
        |
        | if not set
        v
issue source template variables?
        |
        | if not set
        v
project default template variables?
        |
        | if not set
        v
system default template variables
        |
        v
+--------------------------+
| CurrentTemplateVariables |
+--------------------------+

No deep merge happens in the template lane. A lower-priority template is used
only when the higher-priority template is not configured.
```

```text
ProfileVariables

global profile variables
        |
        v
project profile variables
        |
        v
issue profile variables
        |
        v
run profile variables
        |
        v
+------------------+
| ProfileVariables |
+------------------+
```

```text
WorkflowEffectiveVariables

CurrentTemplateVariables
        |
        v
+-----------------------------------------------+
| deep merge                                    |
| base    = CurrentTemplateVariables            |
| overlay = ProfileVariables                    |
| conflict: ProfileVariables wins               |
+-----------------------------------------------+
        ^
        |
ProfileVariables
        |
        v
+----------------------------+
| WorkflowEffectiveVariables |
+----------------------------+
```

```text
WorkflowStageEffectiveVariables

WorkflowEffectiveVariables.vars
        |
        v
+-----------------------------------------------+
| deep merge                                    |
| base    = WorkflowEffectiveVariables.vars     |
| overlay = WorkflowEffectiveVariables          |
|           .stages[stage].vars                 |
| conflict: stage variables wins                |
+-----------------------------------------------+
        ^
        |
WorkflowEffectiveVariables.stages[stage].vars
        |
        v
+---------------------------------+
| WorkflowStageEffectiveVariables |
+---------------------------------+
```

deepMerge：对象递归，后者覆盖同名 key。effective variables 解析不定义 `null`
覆盖语义；variables 存储层应忽略值为 `null` 的 key。

## 加载

```text
LoadTemplate(runId):                          adapter, live
  issue.Template?           → parsed (自定义)
  issue.SourceTemplateId    → project_templates
  project.DefaultTemplateId → project_templates
  else                      → mohist/local (应用配置层)

ResolveCurrentTemplateVariables(runId):       adapter, live, fallback selection
  issue custom template vars?
  else issue source template vars?
  else project default template vars?
  else system default template vars

ResolveProfileVariables(runId):               adapter, live, layered deepMerge
  global config > project vars > issue vars > run vars

ResolveWorkflowEffectiveVariables(runId):
  deepMerge(ResolveCurrentTemplateVariables, ResolveProfileVariables)

ResolveWorkflowStageEffectiveVariables(runId, stage):
  deepMerge(ResolveWorkflowEffectiveVariables.vars, ResolveWorkflowEffectiveVariables.stages[stage].vars)

grain dispatch (纯计算):
  vars    = ResolveWorkflowStageEffectiveVariables(runId, stage)
  payload = vars + { vars } + dispatch context(workflow/stage/work/issue/workspace/tasks)
  with    = Workflow.Domain.ExpandTaskWith(vars, task.With)
```

`dispatch context` 不是 profile variables。它只在 dispatch payload 中存在，例如 `workflow.runId`、`stage.name`、`work.id`、`issue.number`、`workspace.branch`、`tasks.*.outputs.*`。

## ExpandTaskWith

```
for (k,v) in taskWith:
  "${{...}}" 整串 → vars 替换，否则保留 (runner 展开)
  object && k∈vars → deepMerge (vars 覆盖)
  else → preserve
```

## 数据模型

```
project_workflow_profile  projectId → DefaultTemplateId, Variables
project_templates         (ProjectId, TemplateId) → Template
issue_workflow_profile    issueId → SourceTemplateId, Template, Variables
workflow_run_profile      workflowRunId → Variables
```

`WorkflowRun.Metadata` 存 projectId/issueId/profile identity 作解析身份，但不存 profile body。

## Runtime Writes

task 成功后，action output 可以通过 `setVars` 写入 run profile：

```yaml
setVars:
  change.id: output.changeId
  change.url: output.changeUrl
```

写入语义：

- 左侧路径相对 `vars`。
- 只 patch `workflow_run_profile.Variables`。
- 不写回 project profile 或 issue profile。
- 不修改 `WorkflowRun` 执行状态。
- 读取后续 task variables 时通过 `ResolveWorkflowStageEffectiveVariables(runId, stage)` 统一合并生效。

`setVars` 是 task 执行的一部分，由 runner 执行。server 通过 dispatch payload 将 `setVars` 映射下发给 runner。runner 在 action 成功后，从 output 按源路径提取变量，通过 `PATCH /api/workflow-runs/{id}/workflow-profile/variables` 写入 run profile，然后才 report task 完成。setVars 失败（路径缺失或 API 错误）导致 task failed。server 不参与 setVars 的路径遍历或提取。

task output context 和 run profile 分离：

- `task.Output`（`JsonElement?`）：action 产出的完整 JSON object，归属 `TaskRun` 执行状态。dispatch context 中通过 `tasks.<taskId>.outputs.*` 访问。
- `vars.*`：来自 `WorkflowStageEffectiveVariables`，是 `WorkflowEffectiveVariables.vars` 再合并当前 stage overrides 后的实际执行变量。

## Read API

```text
GET /workflow-runs/:id/variables/effective
  returns WorkflowEffectiveVariables.vars
  does not read current stage
  does not merge WorkflowEffectiveVariables.stages[*].vars

GET /workflow-runs/:id/variables/effective?stage=build
  returns WorkflowStageEffectiveVariables
  merges WorkflowEffectiveVariables.vars with WorkflowEffectiveVariables.stages["build"].vars

GET /workflow-runs/:id/variables/effective/:keyPath
  returns the value at keyPath from WorkflowEffectiveVariables.vars
  returns null when keyPath is missing

GET /workflow-runs/:id/variables/effective/:keyPath?stage=build
  returns the value at keyPath from WorkflowStageEffectiveVariables
  returns null when keyPath is missing
```

## Write API

```text
系统模板      GET /workflow-templates/system
项目模板      /projects/:p/workflow-templates          (GET, POST; /:t GET,PUT,DELETE)
项目 profile  /projects/:p/workflow-profile            (GET; /default-template PUT,DELETE; /variables GET,PUT,PATCH)
issue profile /projects/:p/issues/:n/workflow-profile  (GET; /template PUT,DELETE; /variables GET,PUT,PATCH)
run profile   /workflow-runs/:id/workflow-profile       (GET; /variables GET,PUT,PATCH)
effective     /workflow-runs/:id                        (/yaml, /variables/effective)
```
