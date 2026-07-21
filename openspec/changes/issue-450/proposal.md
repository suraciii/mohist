## Why

Workflow users can currently delegate an Inline Agent turn only to OpenCode. The Pi product and runtime contracts are already settled, so the direct Workflow path can now add Pi without introducing another agent ownership model, completion protocol, or Session event store.

## What Changes

- Add `mohist/pi` to the existing Workflow Action contract for both tasks and checks. It accepts `prompt`, optional logical `session`, and optional `options.model` / `options.variant`; task completion and public output remain owned by the Workflow executor.
- Bundle an exact Pi SDK version in the Runner, raise the Node floor to the SDK requirement, and smoke-verify the real SDK surface before PiRuntime implementation. Pi readiness joins the existing Runner work-claim gate.
- Add an in-process `PiRuntime` that creates or restores Pi session files, applies model and thinking level per turn, submits prompts literally, executes tools unattended, projects runtime facts, enforces the Workflow deadline, and normalizes provider failures.
- Make Workflow AgentSession open and attach runtime-aware. Persist the Pi session-file path before the first prompt, reuse it across tasks, retries, model changes, cleanup, and Runner restart, and reject missing or incompatible bindings without silent replacement.
- Serialize complete task and check turns per logical Workflow Session with one process-local Runner coordinator shared by OpenCode and Pi. Different logical Sessions remain concurrent.
- Send Pi input, assistant/reasoning text, tool activity, model observations, token usage, and cost through the existing physical-binding-validated AgentSession runtime-event route. A required reporting failure fails the current work; this change adds no Action stream, outbox, cursor, inventory, checkpoint, or restart replay protocol.
- Update the existing runtime-neutral Web Session presentation and Workflow Inline Agent classification for Pi.
- Keep Mohist Agent execution, Pi Session commands, runtime-aware model catalog/UI, ACP/RPC, and a generic AgentRuntime abstraction out of scope.

## Capabilities

- `pi-runtime`: Pinned, in-process Pi execution with readiness, trusted configuration boundaries, persistent physical Session handling, literal prompt completion, unattended tools, deadlines, event normalization, and provider failure policy.
- `pi-workflow-action`: `mohist/pi` as the existing Workflow Action contract in task and check hosts, including input validation, final-text completion, promise projection, recovery errors, and cleanup reuse.
- `pi-workflow-session`: Runtime-aware Workflow AgentSession binding, same-name continuity, process-local turn serialization, and Pi facts in existing Session audit views.

## Impact

- **Runner** (`packages/runner`): pins the Pi SDK, raises the Node engine floor to `>=22.19` across manifests, version pins, CI, Docker, and source-install guidance, adds `runtime/pi`, starts and gates on Pi readiness, shares a Workflow Session turn coordinator across task/check hosts, registers `mohist/pi`, and reports Pi events through `ServerConnection`.
- **Server** (`packages/server`): accepts `pi` on Workflow Session open/attach, preserves guarded physical-binding and lineage rules, recognizes `mohist/pi` as UserFacing, and carries Pi cache-write usage through existing Session contracts.
- **Web** (`packages/web`): recognizes Pi Inline Agent work and renders Pi facts with existing Session components; no Pi-specific Session page or model selector is added.
- **Persistence and APIs**: existing logical AgentSession identity, runtime lineage, `runtimeSessionId`, Runner binding, and immutable work directory remain authoritative. For Pi, `runtimeSessionId` is the absolute session-file path. Pi authentication stays inside the SDK boundary; no credential, Action-stream, or durable Runner reporting schema is introduced.
- **Verification**: the SDK smoke may use the configured Pi environment, while product tests use injected SDK/runtime services, fake clocks, and fake Server connections with no real provider, network, process, Git, database file, or wall clock.
