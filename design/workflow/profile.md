---
purpose: "Workflow profile：目标架构、合并与加载。"
style: ["极简，只给目标态。"]
---

# Workflow Profile

> profile = **template**（选哪个 `WorkflowDefinition`）+ **variables**（`VariableBundle`）。prompts 不属 profile → [`prompt-management.md`](../prompt-management.md)。
> action input/output 契约见 `actions.md`。内置 workflow 行为见 `builtin-workflows.md`。

## 架构

```
Issue ──▶ Workflow                                      依赖方向，单向
WorkflowGrain ──▶ IWorkflowProfileProvider              端口，Workflow 定义，返回仅 Workflow 类型
                       ▲ 实现（DI 组合层）
                WorkflowProfileProvider                  adapter
                       │ live 读 config
                       ▼
        global config · project profile · issue profile · project templates
```

- Workflow 零 Issue 依赖；解析在 adapter，live 不快照。
- 身份：grain 直传 in-memory `WorkflowRun.Metadata`，不反查 `db.Issues` / `Issue.State`。

## VariableBundle

`{ vars, stages: { "plan": { vars } } }`。Set 整替，Patch deep merge。

## 合并

```
低 ──────────────────────────────────────────────────────▶ 高
template embedded (YAML variables:) + project vars + issue vars + dispatch 注入(runId/stage/work，仅 dispatch)
```

deepMerge：对象递归，后者覆盖同名 key。

## 加载

```
LoadTemplate(metadata):                       adapter, live
  issue.Template?           → parsed (自定义)
  issue.SourceTemplateId    → project_templates
  project.DefaultTemplateId → project_templates
  else                      → mohist/default (应用配置层)

LoadVariables(metadata):                      adapter, live, 3 层 deepMerge
  global config > project vars > issue vars

grain dispatch (纯计算):
  resolved = deepMerge(template.embedded, LoadVariables) + dispatch 注入
  with     = Workflow.Domain.ExpandTaskWith(resolved, task.With)
```

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
```

`WorkflowRun.Metadata` 存 projectId/issueId 作解析身份。

## Write API

```
系统模板      GET /workflow-templates/system
项目模板      /projects/:p/workflow-templates          (GET, POST; /:t GET,PUT,DELETE)
项目 profile  /projects/:p/workflow-profile            (GET; /default-template PUT,DELETE; /variables GET,PUT,PATCH)
issue profile /projects/:p/issues/:n/workflow-profile  (GET; /template PUT,DELETE; /variables GET,PUT,PATCH)
run           /workflow-runs/:id                        (/yaml, /variables/effective)
```
