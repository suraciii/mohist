---
name: mohist
description: 执行 Mohist 当前 .NET 后端/API/Web 相关操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 Mohist issue/workflow 的操作时使用。旧 Node CLI 已移除。
hidden: true
---

# mohist

旧 Node CLI 已移除，不要再使用 `node packages/cli/bin/mo` 或 `mo-server`。

当前操作应优先通过：

- ASP.NET Core API: `http://localhost:3456/api/...`
- Server: `dotnet run --project packages/server/src/Mohist.Server/Mohist.Server.csproj`
- Runner: `dotnet run --project packages/runner/src/Mohist.Runner.Cli/Mohist.Runner.Cli.csproj`
- Tests: `dotnet test packages/server/Mohist.sln` 和 `dotnet test packages/runner/tests/Mohist.Runner.Tests/Mohist.Runner.Tests.csproj`

如果用户明确要求旧 `mo` 命令，说明它已经废弃，并改用当前 API 或 Web UI 路径完成同等操作。
