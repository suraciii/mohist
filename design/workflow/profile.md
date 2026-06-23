---
purpose: "Workflow profile：目标架构、合并与加载。"
style: ["极简，只给目标态。"]
---

# Workflow Profile

> profile = **template**（选哪个 `WorkflowDefinition`）+ **variables**（`VariableBundle`）。
> prompts 不属 profile → [`prompt-management.md`](../prompt-management.md)。
> action input/output 契约见 `actions.md`。内置 workflow 行为见 `builtin-workflows.md`。

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
vars.github.pr.number
vars.github.pr.url
vars.github.pr.headSha
```

run profile 不属于 `WorkflowRun` aggregate state。`WorkflowRun` 通过 `WorkflowRunId` 关联它，profile service 负责读写。

## 合并

```
低 ──────────────────────────────────────────────────────▶ 高
template embedded (YAML variables:) + global vars + project vars + issue vars + run vars + dispatch context
```

deepMerge：对象递归，后者覆盖同名 key。

## 加载

```
LoadTemplate(runId):                          adapter, live
  issue.Template?           → parsed (自定义)
  issue.SourceTemplateId    → project_templates
  project.DefaultTemplateId → project_templates
  else                      → mohist/default (应用配置层)

LoadVariables(runId):                         adapter, live, layered deepMerge
  global config > project vars > issue vars > run vars

grain dispatch (纯计算):
  resolved = deepMerge(template.embedded, LoadVariables)
  payload  = resolved + dispatch context(workflow/stage/work/issue/workspace/tasks)
  with     = Workflow.Domain.ExpandTaskWith(resolved, task.With)
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
  github.pr.number: output.prNumber
  github.pr.url: output.prUrl
```

写入语义：

- 左侧路径相对 `vars`。
- 只 patch `workflow_run_profile.Variables`。
- 不写回 project profile 或 issue profile。
- 不修改 `WorkflowRun` 执行状态。
- 读取后续 task variables 时通过 `LoadVariables(runId)` 统一合并生效。

`setVars` 是 task 执行的一部分，由 runner 执行。server 通过 dispatch payload 将 `setVars` 映射下发给 runner。runner 在 action 成功后，从 output 按源路径提取变量，通过 `PATCH /api/workflow-runs/{id}/workflow-profile/variables` 写入 run profile，然后才 report task 完成。setVars 失败（路径缺失或 API 错误）导致 task failed。server 不参与 setVars 的路径遍历或提取。

task output context 和 run profile 分离：

- `task.Output`（`JsonElement?`）：action 产出的完整 JSON object，归属 `TaskRun` 执行状态。dispatch context 中通过 `tasks.<taskId>.outputs.*` 访问。
- `vars.*`：来自 profile variables，包含 project/issue/run 三层变量。

## Write API

```
系统模板      GET /workflow-templates/system
项目模板      /projects/:p/workflow-templates          (GET, POST; /:t GET,PUT,DELETE)
项目 profile  /projects/:p/workflow-profile            (GET; /default-template PUT,DELETE; /variables GET,PUT,PATCH)
issue profile /projects/:p/issues/:n/workflow-profile  (GET; /template PUT,DELETE; /variables GET,PUT,PATCH)
run profile   /workflow-runs/:id/workflow-profile       (GET; /variables GET,PUT,PATCH)
effective     /workflow-runs/:id                        (/yaml, /variables/effective)
```
