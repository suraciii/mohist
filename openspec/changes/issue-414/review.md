# Review - issue-414

## Findings

### F-1 [BLOCKING] The migration snapshot does not match the current EF model, so server integration tests and application startup fail

- **Where:** `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260718100000_AddRoutingRules.Designer.cs`, `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/MohistDbContextModelSnapshot.cs`, and `packages/server/src/Mohist.Server/Infrastructure/Data/Db/MohistDbContext.cs:180-183`.
- **Problem:** The current model declares `AgentSessions.LabelTriggerRuleId`, but the final migration designer/snapshot still describes the pre-change `LabelTriggerSubscriptionId`. EF consequently raises `PendingModelChangesWarning` as an exception when `DatabaseInitializer` runs. In addition, test databases that use the migration chain lack `LabelTriggerRuleId`, producing SQLite `no such column` failures when sessions are queried or stored.
- **Impact:** `npm test -- --no-restore` fails (1147 server spec failures), and a deployment cannot initialize its database normally. Regenerate/fix the migration designer and model snapshot so the final migration chain and the runtime model agree, including the new computed-column expression for `mohist.io/trigger/rule-id`.

### F-2 [BLOCKING] The rule API ignores the CLI's documented `--before` / `--after` create syntax

- **Where:** `packages/cli/Mohist.Cli/MohistCliCommands.Routing.cs:54-60` sends ordering as `?before=` / `?after=`, while `packages/server/src/Mohist.Server/Api/RoutingRulesRoutes.cs:22-35` passes only `request.Before` / `request.After` to `RoutingRuleStore.CreateAsync`.
- **Problem:** `RoutingRuleCreateRequest` is populated from the JSON body, not the query string. The CLI never includes `before` or `after` in that body, so a command such as `mo routing rule create ... --before rule_a` always appends instead of inserting before `rule_a`.
- **Impact:** This violates the routing-rule command contract and means operators cannot configure the targeted-over-fallback ordering at creation time. Bind ordering consistently on one transport surface, and cover the actual CLI-to-server request shape in an integration test.

### F-3 [BLOCKING] `mo routing test` table output is incompatible with the server response and shows no rule trace

- **Where:** `packages/cli/Mohist.Cli/MohistCliCommands.Routing.cs:173-190` versus `packages/server/src/Mohist.Server/Api/RoutingTestRoutes.cs:43-50,99-114`.
- **Problem:** The server returns each event's rules in `rules`, with `wouldTriggerAgent`; the CLI renderer looks only for `outcomes`, and its fallback names (`agentName` / `resolvedAgentName`) do not include `wouldTriggerAgent`. For a real successful response, it prints the event header and then returns without printing any compared rules, matches, continue/stop decisions, or Agents.
- **Impact:** The required human-readable dry-run trace is unusable in the default CLI output. Make the renderer consume the endpoint's `rules` schema and include the trace fields required by the issue; update the CLI spec fixture to use the real response shape.

### F-4 [BLOCKING] Dry-run does not replay the complete population that live routing dispatch handles

- **Where:** `packages/server/src/Mohist.Server/Agent/Services/ProjectRecentEventReader.cs:23-42`.
- **Problem:** Live dispatch subscribes to every project-stamped CloudEvent (`RoutingDispatchHandler` has `[Subscription(Type = "*")]`), but the dry-run reader only pulls four aggregate tables. Project-scoped event families stored elsewhere, or live events not persisted in those tables, cannot be replayed even though the routing table will process them in production.
- **Impact:** `mo routing test` can report no event/hit for a rule that will trigger on a real project event, contradicting the acceptance criterion that dry-run conclusions match real dispatch. Source dry-run events from the full persisted dispatchable-event population, or ensure every dispatchable project event is persisted into the reader's source and add cross-family parity tests.

### F-5 [BLOCKING] Legacy placeholder aliases erase absent values instead of preserving the placeholder

- **Where:** `packages/server/src/Mohist.Server/Events/Subscriptions/ResponsePromptRenderer.cs:70-71`.
- **Problem:** `{{event.*}}` correctly preserves an absent attribute verbatim, but legacy aliases are replaced with `string.Empty` when their envelope attribute is absent. For example, `{{stage}}` becomes empty rather than remaining visible.
- **Impact:** The issue requires placeholders with no value to remain unchanged while retaining support for legacy placeholders. Apply the same presence-preserving behavior to `{{workflow_run_id}}`, `{{stage}}`, and `{{event_type}}`, and add absent-alias coverage.

### F-6 [BLOCKING] The required AgentJob-to-trigger lookup is not implemented

- **Where:** `packages/server/src/Mohist.Server/Events/Subscriptions/RoutingDispatchHandler.cs:62-67`, `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuery.cs:129-130`, and `packages/server/src/Mohist.Server/Api/AgentJobController.cs`.
- **Problem:** The event and rule ids are only stored as `AgentSession` labels. The only new lookup support is a session query filter; no AgentJob response/query maps an AgentJob to its session trigger labels, and no event query enumerates the triggered jobs with rule ids.
- **Impact:** This misses the explicit bidirectional visibility acceptance criterion: event -> rules/AgentJobs and AgentJob -> event/rule. Add a query/API representation that follows the job's `AgentSessionId` and exposes both trigger identifiers, then cover both directions end-to-end.

<promise>FAIL</promise>
