## Why

The runner list shows presence and health, but not *what* a runner is doing. When a runner is busy, users must leave the page and dig through workflow logs to learn which stage and which issue it is running. The runtime state also only surfaces the first active work per runner — so when a runner uses multiple workflow slots, the rest are invisible. Users need to see each runner's full active-work context in one place, and be able to drill into a single runner without leaving the system.

## What Changes

- Enrich each runner's active-work context with the work identifier, work type, stage, title, and associated issue reference — sourced from information already carried on `WorkDispatch` at dispatch time (no new runner→server reporting).
- Surface **all** active works per runner (one per slot), not just the first; each is presented independently.
- Add a server endpoint to query a single runner's full detail by runner id (identity, capabilities, all active-work context, health metrics).
- Add a Web UI runner detail page (full identity, capabilities, all active works with jump-to-issue links, health metrics), reachable by clicking a runner in the list.
- Add a `mo runner show <runnerId>` CLI subcommand that prints a single runner's full detail.

Scope is strictly read-only observability. No control actions, no historical execution records, no changes to the registration / heartbeat / dispatch protocol, and no real-time log streaming.

## Capabilities

### New Capabilities

- `runner-detail`: Single-runner observability — full identity (kind, hostname, scope, registeredAt, build git hash), capabilities (capabilities, coder models, max slots), every active work's context (workId, workType, stage, title, issue reference), and health metrics (status, connection state, last heartbeat). Covers list-level active-work enrichment, the single-runner detail query, the Web detail page, and the CLI `show` subcommand. Bounded by each runner's max workflow slot count.

### Modified Capabilities

_(None — no existing spec covers runner observability behavior. `http-api`, `web-ui`, and `cli-interface` have no current runner-status requirements to amend; the new behavior is captured entirely by `runner-detail`.)_

## Impact

- **Server / Runner domain**: `RunnerRuntimeState` and `RunnerStatusService` projection gain full active-work context (workId, workType, stage, title, issue ref) and multi-work emission, drawn from the existing `RunnerTrackedWork.Dispatch` payload already held in the `RunnerGrain`. The `RunnerActiveWorkView` shape becomes a list and gains an issue reference field.
- **HTTP API**: New single-runner detail endpoint under `/api/projects/{projectRef}/runners/{runnerId}`; existing list endpoint returns enriched, multi-item active work per runner.
- **Web UI**: New runner detail page (route + query) and list-to-detail navigation; active-work rows render stage, title, and a link to the associated issue.
- **CLI**: New `mo runner show` subcommand in the existing top-level runner command group, consuming the new detail endpoint while keeping service-lifecycle subcommands untouched.
- **Runner package**: No protocol change — active-work context is derived from data already present at dispatch time.
