---
name: mohist
description: 执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。
---

# mohist

Use this skill for current Mohist .NET backend, API, Web UI, and workflow operations.

Current execution paths:

- Server: `dotnet run --project packages/server/src/Mohist.Server/Mohist.Server.csproj`
- Tests: `dotnet test Mohist.sln`
- Web UI: `npm run dev:web`
- Runner: `npm run dev:runner`

Prefer the current `mo` CLI, ASP.NET Core API, or Web UI workflows. Do not instruct the user to use the removed pre-Orleans Node CLI.

When operating on Mohist issues or workflows:

- Use the current `mo` CLI command surface and current .NET backend behavior as the source of truth.
- Treat local issue artifacts under `openspec/changes/<issue>/` as authoritative when the user provides an issue context.
- Keep changes scoped to the current issue; do not substitute adjacent cleanup or legacy behavior unless the issue explicitly requires it.
- For local verification, prefer the smallest relevant command or test filter instead of broad full-repo runs.

Boundaries:

- Do not rely on the removed pre-Orleans Node CLI or its workflow/runtime behavior.
- Do not assume Mohist server APIs must be running for purely local CLI or filesystem tasks unless the task explicitly requires server interaction.
- Do not mutate internal runtime data under `.mohist/skills` when the task is about shared coder-agent skills.
