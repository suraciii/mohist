## Why

Each issue's autopilot today requires a project-level routing rule: there is no per-issue
toggle, and no way to say "this one issue, I'll handle myself" without disturbing the
global rule that governs every other issue. Issue #489 adds the missing per-issue control
surface — an Agent can watch a single issue (auto-responding at approval gates and run
failures), and a single issue can be muted against a project-level rule without touching
the rule itself. The design contract is already fixed in [`design/issue-watch.md`](../../design/issue-watch.md).

## What Changes

- New `mo issue watch add <issue> --agent <name>` command: creates a `watching` declaration
  for that issue, or unmutes a previously muted Agent on that issue. Idempotent when already
  watching (reports current state).
- New `mo issue watch remove <issue> --agent <name>` command: deletes a `watching`
  declaration; when the Agent is otherwise covered only by a project-level routing rule,
  records a `muted` declaration (the global rule stays, this one issue is excepted).
  Idempotent when already muted (reports current state).
- New `mo issue watch list <issue>` command: lists the issue's `watching` and `muted` Agents.
- `mo issue view <issue>` and the Web issue detail gain read-only "关注 / 静音"
  (watching / muted) sections projected from the new declarations.
- New `WatchEntry` resource: `(ProjectId, IssueNumber, AgentId, State: watching | muted)`,
  owned by the Agent context; the Issue aggregate does not hold it.
- Event dispatch gains two behaviors on events carrying an issue:
  - **Muted suppression**: a routing-rule hit for an Agent that is `muted` on that issue is
    treated as a non-match (same style as an archived Agent), with a structured log.
  - **Watching launch**: on the fixed event set `stage.approval-requested` and `run.failed`,
    each `watching` Agent for that issue is launched using the built-in response prompt
    (event fact + "act on your identity instructions"); no per-watch `ResponsePrompt`.
- Launch idempotency is normalized to `(projectId, eventId, agentId)`: the same Agent hit by
  both a routing rule and a watch on one event is launched at most once. Trigger labels
  annotate the source as watch so event↔session traceability is preserved.
- `add` / `remove` validate the Agent exists and is active; archived Agents are rejected.
- Watch launches reuse the routed launch path — workspace resolution, preflight-failure
  handling, and trigger tagging are identical to routing-rule launches.

## Capabilities

- `issue-watch`: The `WatchEntry` resource — its states (`watching`, `muted`), the
  `mo issue watch add | remove | list` command semantics (the state-machine transitions and
  idempotency), active-Agent validation, and the read projection that surfaces watching /
  muted in `mo issue view` and the Web issue detail.
- `issue-watch-dispatch`: The runtime behavior of watch declarations at event time — muted
  suppression of routing-rule hits, watching-triggered launches on the fixed event set
  (`stage.approval-requested`, `run.failed`), idempotency normalization so one Agent is
  launched at most once per event, and trigger-label provenance marking the launch source as
  watch.

## Impact

- **CLI** (`packages/cli/`): new `mo issue watch` command group
  (`MohistCliCommands.Issue.Watch.cs`); `RenderIssueShow` in
  `TableRenderer.Issues.cs` gains watching/muted rendering; Agent resolution reuses
  `ResolveAgentAsync` from `MohistCliCommands.Agent.cs`.
- **Server — Agent context** (`packages/server/src/Mohist.Server/Agent/`): new
  `WatchEntry` domain model, `WatchEntryStore` (`IScopedService`, mirroring
  `RoutingRuleStore`), and validation; reuses `IAgentLauncher.LaunchRoutedAsync`.
- **Server — dispatch** (`packages/server/src/Mohist.Server/Events/Subscriptions/`):
  `RoutingDispatchHandler.DispatchAsync` gains muted suppression before launch and a
  watching-launch pass after the rule loop; trigger-label provenance via
  `GenericAgentSessionMetadata`.
- **Server — Issue read model** (`packages/server/src/Mohist.Server/Issue/Services/`):
  `IssueReadModel` / `IssueQuerier.EnrichAsync` assemble watching/muted projection.
- **Server — API**: new issue-watch routes mounted under `IssueRoutes`.
- **Server — persistence**: new `WatchEntryRow` + EF migration.
- **Web** (`packages/web/`): issue detail renders watching/muted from the existing read
  model (no new API; read-only this issue).
- No new external dependencies; no public API removals or breaking changes.
