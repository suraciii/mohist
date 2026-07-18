## Context

The [proposal](proposal.md) and capability [specifications](specs) extend the repository resource model delivered by issue 416. A Project can now declare several named repositories with exactly one default, but repository handling is still only partially Issue-aware:

- `Issue.RepositoryRef` is nullable. HTTP creation resolves a repository, but direct grain creation can persist an unknown or non-canonical value; PATCH, list filtering, and CLI output do not support the binding lifecycle.
- The current workflow start resolves the selected repository but writes its routing facts into mutable Issue profile variables. `WorkflowRun` persists a workspace but not the repository name, Git URL, or base branch, and review/rebase routes later combine that workspace with live Project metadata.
- Runner workspaces are keyed by Project name and Issue number. Their markers omit Project and repository identity, and the normal preparation path can reuse a directory before fully validating it.
- Integrate locks are Project-wide, so delivery in one repository can block an unrelated repository in the same Project.
- Repository deletion checks only existence and default status. A query immediately before deletion is insufficient because Issue creation, reassignment, or reopen can commit after that query.

The implementation spans the Issue, Project Space, and Workflow bounded contexts plus the Runner process and CLI. The Server remains the state authority; the Runner performs Git side effects from Server-provided facts. An Issue owns only its stable repository resource name, Project Space owns mutable repository metadata, and each WorkflowRun owns the metadata snapshot used by that run.

The aggregate rules in `design/architecture.md` are binding constraints: one transaction writes one aggregate, authority grains remain non-reentrant, and no join table or handler may create a cross-aggregate transaction. Workflow startup follows `design/workflow/issue-coordination.md`: the Issue commits first and a durable handler idempotently creates the WorkflowRun. New contracts use the canonical Issue identity `(ProjectId, IssueNumber)` from `design/conventions.md`; they do not extend the temporary random Issue ID still present in current code. Persisted Issue and Workflow state from before this change is not a compatibility target.

## Goals / Non-Goals

**Goals:**

- Make one canonical Project repository name required Issue state, with default selection at creation, validated pre-start reassignment, and a permanent lock at the first committed workflow start.
- Expose and filter the stored target independently from whether its current Project declaration can still be resolved.
- Give every Issue-backed WorkflowRun one immutable repository context and route workspace, review, rebase, cleanup, local integration, and pull-request operations through it.
- Prevent repository deletion from racing into an orphaned non-terminal Issue without violating aggregate transaction boundaries.
- Isolate Runner workspace identity and Integrate coordination by workflow run and repository while preserving implicit single-repository use.
- Align the API and `mo` CLI with `--repo` and actionable repository-in-use failures.

**Non-Goals:**

- Checking out multiple repositories for one Issue or automating cross-repository integration verification.
- Adding repository selection or management UI to the Web application.
- Freezing all Issue content, prompts, or ordinary workflow variables at run start; only routing identity and repository/workspace facts become authoritative runtime context.
- Changing whole-Project deletion semantics. This change guards removal of a repository member while its Project exists; Project deletion remains separate work.
- Migrating or inferring target/started state for persisted pre-change Issues, active runs, or Runner workspaces.
- Adding a new Runner endpoint, transport, message family, or package dependency. Existing workspace-query payloads gain required identity fields in place.

## Decisions

### D1: The Issue stores a required canonical name and a permanent start fact

Keep `IssueRepositoryRef` as the internal value object, but make it non-null for newly created state. `Issue.Create` receives a repository name already resolved against the owning Project and persists the declaration's canonical casing. Resolution has two explicit modes:

- creation selection accepts an omitted name and resolves the current default;
- stored-target resolution requires a non-empty name and never falls back to the default.

Add `HasWorkflowStarted` to the Issue aggregate. `Issue.Start` sets it in the same transaction that stores the first `WorkflowRunId`, enters `in_progress`, and appends `IssueWorkStarted`; no later transition clears it. Repository reassignment checks this fact rather than `WorkflowRunId` or status, because a stopped run can clear its current reference and a cancelled started Issue can return to `backlog`.

Add `ChangeRepository(canonicalName)` and `IssueRepositoryChanged(oldName, newName)`. When PATCH includes `repositoryName`, its coordinated participant command carries the complete set of aggregate-owned PATCH fields and applies them in one Issue transaction. External profile and attachment inputs are prevalidated; their existing post-commit side effects run only after the coordinated Issue result is known. Reopen first verifies that the retained target still exists, then performs the existing cancelled-to-backlog transition. Start and reassignment remain serialized by the non-reentrant Issue grain; the transition that commits first determines the outcome.

The serialized Issue shape also gains a coordination revision and receipt described in D2. New state requires these fields. Missing pre-change target/start fields are treated as unsupported state rather than deserializing an old started Issue as unlocked.

**Alternative considered:** derive immutability from current `WorkflowRunId`, `in_progress`, or completion timestamps. Rejected because all can be absent or changed after a real start. Storing Git URL and base branch on the Issue was also rejected because those facts remain Project-owned and may change before a later run.

### D2: A persisted Project-scoped coordinator serializes binding and deletion commands

Add a non-reentrant `IIssueRepositoryCoordinatorGrain` keyed by `ProjectId`. All operations that can establish a non-terminal binding, plus repository removal, enter through this grain:

```text
Create Issue       -> coordinator -> Issue participant
Reassign target    -> coordinator -> Issue participant
Reopen cancelled   -> coordinator -> Issue participant
Remove repository  -> coordinator -> Project participant
```

Completion and cancellation continue to call the Issue directly because they only remove blockers. Start also remains direct: a backlog Issue already protects its repository, and Issue-grain serialization resolves its race with reassignment.

This grain is a durable application process manager, not a new business authority: it owns only uncertain command delivery, writes at most one participant aggregate per command, and stores no repository or Issue facts. It lives downstream of the Issue and Project participant interfaces; neither participant references or calls back into it. Before implementation, `design/architecture.md` and the context map must record this narrow process-manager pattern and its prohibition on multi-aggregate writes or synchronous callbacks.

The coordinator persists one technical fence:

```text
RepositoryCoordinatorState
  Pending?: { commandId, kind, payload, expectedRevision }
```

For each command it resolves and canonicalizes the repository, captures the participant revision, persists `Pending`, invokes one idempotent participant command, then clears `Pending` after a definitive applied or rejected result. Before accepting a new command, it replays any pending command. Timeout, activation loss, or an unknown downstream result leaves the fence in place.

Issue state gains `RepositoryBindingRevision` and `LastRepositoryCommand`; Project persistence gains `RepositoryRevision` and `LastRepositoryCommand`. A receipt contains the command ID, kind, repository name, and applied revision. The participant performs this sequence in its own transaction:

1. Return already-applied when the receipt command ID matches.
2. Reject a stale expected revision.
3. Revalidate its aggregate invariants.
4. Apply the mutation, increment the revision, and persist the receipt with state and events.

Issue coordination revision changes on create, repository reassignment, first start, completion, cancellation, and reopen. Project repository revision changes on add, metadata update, default selection, and removal. Even a successful no-op reassignment records a receipt, so a lost response cannot later replay as a post-start lock failure. Rejected commands write no receipt; replay either reproduces the rejection at the same revision or reports stale state after another transition.

Repository removal checks existence and default status first, then queries committed `IssueRow` state for the canonical Project/repository pair and statuses `backlog` or `inProgress`. If a blocker exists, the coordinator returns a distinct repository-in-use conflict without fencing a Project mutation. Otherwise it fences and calls the idempotent Project removal participant, which rechecks its own repository invariants before committing. This preserves existing not-found/default error precedence.

Issue number allocation remains before the coordinated create call. A failed create can consume a number, which is already permitted; the fixed `(ProjectId, IssueNumber)` identity is retained in the pending command for replay. A repository-bearing PATCH persists the complete aggregate patch in that command, so an ambiguous result cannot commit only repository reassignment while dropping sibling Issue fields. Narrow participant interfaces and an architecture test prevent production routes or services from bypassing the coordinator methods.

**Alternatives considered:** a pre-delete query alone has a commit race. A stateless non-reentrant coordinator cannot distinguish a timed-out participant that later commits, allowing deletion to overtake it. Long-lived Project usage claims duplicate every non-terminal Issue binding and require release/reconciliation on all terminal paths. The persisted short-lived fence keeps Issue rows authoritative and writes coordination state only while an outcome is uncertain.

### D3: A generated Issue projection supports reads and deletion checks

Add required `RepositoryName` to `IssueInfo` and `IssueReadModel`, populated directly from stored Issue state. Keep resolved `Repository` metadata nullable and retain `RepositoryProblem` for an unresolved historical target. Read APIs and CLI rendering use `RepositoryName`, never the default repository, as the target identity.

Add a stored generated `IssueRow.RepositoryName` column projected from the Issue JSON state and index `(ProjectId, RepositoryName, Status)`. `State` remains the only writable Issue authority. Repository deletion uses an exact canonical-name lookup on this projection and authored Issue status; it does not use workflow health or enriched status. Issue list filtering applies `OrdinalIgnoreCase` to stored names and does not require a live declaration, preserving discoverability of terminal historical targets. It composes with the existing list filters.

The migration also adds Project repository revision/receipt columns used by D2. Coordinator pending state uses the existing Orleans ADO.NET grain storage and needs no feature-specific table.

**Alternative considered:** a writable Issue-to-repository join table would duplicate aggregate state and violate the transaction rules. Resolving the current default during reads or filters would silently retarget history. An entirely in-memory deletion scan was rejected because the coordinator needs a focused committed-state query and an indexable blocker check.

### D4: WorkflowRun owns an immutable repository context

Add an optional `WorkflowRepositoryContext` to `WorkflowStartInput` and `WorkflowRun` beside the existing workspace:

```text
WorkflowRepositoryContext
  Name
  GitUrl
  BaseBranch
  RemoteFingerprint
  RemoteIdentityVersion
```

`RemoteFingerprint` is a SHA-256 digest of a versioned, credential-free normalization of the Git remote. Issue-backed starts must provide both repository and workspace contexts; generic workflows may leave them absent.

Workflow startup is Issue-first:

1. The start command validates startability, resolves the current declaration for the stored target, computes the remote fingerprint and run-unique workspace, and prepares the complete immutable start input.
2. One Issue transaction allocates/stores the `WorkflowRunId`, sets `HasWorkflowStarted`, enters `in_progress`, and appends `IssueWorkStarted` carrying the repository/workspace snapshot and typed Issue context.
3. The durable handler rereads the Issue, ignores an event that no longer names its active run, and calls input-idempotent `WorkflowRun.EnsureStarted` with that snapshot.
4. `EnsureStarted` creates the run once; a duplicate with identical input is success, while the same run ID with different context is corruption. Dispatch is impossible before this WorkflowRun transaction commits.

Repository resolution or any other failure before step 2 leaves the Issue unlocked. Handler, run-creation, or workspace failure after step 2 leaves it locked and converges through event replay/retry. `WorkflowRun` assigns the repository context once, and normal run commands cannot mutate it. The run store already serializes aggregate JSON, so this adds no relational schema change. A later run resolves updated Project metadata; an existing run keeps its original values.

Routing variables become a final authoritative overlay after template, global, Project, Issue, run, and stage variables are merged. The overlay replaces the complete `repository` and `workspace` roots from `WorkflowRun.Repository` and `WorkflowRun.Workspace`, and resets `mohist.runId`, `project.id`, and `issue.number` from run identity/metadata. One helper supplies both effective-variable reads and dispatch/task `with` expansion so displayed and executed values cannot diverge. Ordinary title, body, prompt, model, CI, and user variable semantics remain unchanged.

Workspace, review, cleanup, and rebase API routes load the run repository/workspace context instead of combining a persisted path with live Project metadata. Cleanup therefore remains possible after a terminal Issue's repository declaration is removed. Omitted rebase base uses the run snapshot; an explicit base remains an operation-local override within the same verified repository and does not change later delivery.

**Alternatives considered:** creating WorkflowRun before committing the Issue permits dispatch while the binding is still unlocked and requires compensation after partial success. Mutable Issue profile variables and live Project lookups can mix metadata within one run and can be overridden by later variable layers. A full `WorkflowRunContext` snapshot was rejected because freezing title, body, prompts, and unrelated content is outside this change; the narrow repository context plus existing workspace is sufficient.

### D5: Runner workspaces are run-unique and prove repository identity

For Issue-backed runs, replace Project/Issue-derived workspace directories with a deterministic run path:

```text
<runnerRoot>/workspaces/run-<sha256(workflowRunId)>
```

The Server persists this path in `WorkspaceIdentity`; the Runner recomputes it from its configured root and the dispatch `workflowRunId`, verifies exact equality and containment, and treats the dispatch envelope run ID as authority. Repository names and user-provided strings never become path segments. Managed parents and final paths are checked with `lstat` plus canonical-parent containment; symlinked paths are rejected.

Workspace creation has a distinct bootstrap transition. When the final path is absent, the Runner creates a deterministic sibling preparation directory, clones the expected remote there, verifies `origin`, writes and verifies the complete marker, then atomically renames the directory to the final run path. A retry may remove only that exact non-symlink preparation path before recreating it. A crash after rename but before registry update is recovered by validating the final marker/origin and rebuilding the registry entry.

Version the Issue workspace marker and registry entry with `workflowRunId`, `projectId`, Issue number, canonical repository name, base branch, run branch, remote fingerprint, and remote-identity version. When the final path already exists, the Runner validates every expected field and verifies actual `origin` against the Server-provided fingerprint before preparation, reuse, reset/abort, review, rebase, cleanup, or deletion. Missing or mismatched final state fails before mutation; it is never replaced with the Project default or an inferred `main` branch.

Extend the existing workspace query payload with the same expected run, Project, Issue, repository, remote fingerprint/version, path, branch, and base-branch identity. Status, diff, commits, commit diff, file content, and cleanup all use that payload and one validation path. This is versioned evolution of existing dispatch/SignalR operations, not a new Runner protocol or message family. `WorkDispatch` continues carrying resolved variables. Generic runs without repository context retain their existing workspace behavior and do not require the Issue marker variant.

**Alternative considered:** adding repository name to the current Issue-number path still collides across reruns and allows one run to delete another run's workspace. Trusting only a marker or only the supplied path leaves origin/path substitution unchecked. A run-hash path plus marker and remote proof closes all three identity gaps without exposing credentials in markers.

### D6: Built-in delivery coordination is scoped by repository resource

For the built-in `project-integration` resource on Issue-backed runs, derive the lock key from `(ProjectId, WorkflowRun.Repository.Name, resource)`. Encode the tuple with a delimiter-safe typed codec and never place a raw URL or credential in lock identity. The canonical Project-local resource name is the product identity: metadata edits do not split an active resource, different named resources in one Project remain independent, and equal names in different Projects remain isolated. Generic runs and custom sequential resources retain the existing Project/resource scope.

The built-in integration resource name remains `project-integration`; only its key derivation changes. Thus delivery for one repository resource does not overlap, while a blocked or failed delivery for another resource cannot hold its coordination key.

Local and GitHub delivery actions continue to execute from the verified workspace, but all repository/base inputs come from the D4 overlay. Remove fallbacks to the Project default or implicit `main`. Pull-request operations remain repository-scoped through the verified workspace remote, so equal pull-request numbers in different repositories cannot collide.

To make resource-name isolation sound, `RepositoryPolicy` validation rejects a repository add or metadata update whose normalized remote fingerprint collides with another declared repository in the same Project. This reuses the credential-free normalizer from D4/D5 and keeps one physical remote addressable by exactly one resource name. The `project-management` capability gains this alias-rejection requirement; `mo repo add`/`update` surface the conflict through existing validation errors.

**Alternative considered:** the current Project-only key is safe but over-serializes unrelated repositories. A physical-remote fingerprint was rejected for coordination because it would make differently named resources block each other and split one named resource when its Git URL changes, contradicting the resource-name contract. Allowing aliases and treating them as unsupported operator configuration was rejected because a risk note cannot override the normative isolation spec; rejecting aliases at declaration time is the smallest invariant that makes the canonical-name lock safe. Custom Project-global locks are not weakened.

### D7: API and CLI changes stay thin over the domain contract

Keep `repositoryName` as the HTTP field. Add it to Issue PATCH and list query contracts, and return it on create, PATCH, list, and detail independently from resolved metadata. PATCH returns the updated Issue with canonical casing, so `WEB` is reported as `web`. Map unknown repository, locked target, missing target on reopen/start, and repository-in-use deletion to typed existing error envelopes; the Project grain remains the final authority for not-found/default deletion outcomes.

Replace Issue CLI `--repository` with `--repo` on create and add `--repo` to update and list. Do not retain an alias. Send `repositoryName` in create/update bodies and list queries, and render the stored name in list/show tables even when metadata is unresolved. `mo repo delete` needs no special success path: existing envelope handling prints the Server's readable in-use failure, exits non-zero, and suppresses success output.

**Alternative considered:** preserving `--repository` as an alias would contradict the deliberate breaking command cleanup. Reconstructing deletion guidance in the CLI would duplicate Server policy and let messages drift from the authoritative failure.

### D8: Verification follows authority boundaries

Add focused coverage at each boundary:

- Issue unit/spec tests cover canonical/default creation, unknown and complete-PATCH atomic reassignment, permanent start locking, start/reassign ordering, reopen validation, events, stored-name reads, and composed filtering.
- Coordinator grain specs use awaitable probes to force both orderings and lost-response points for create/delete, reassign/delete, and reopen/delete. Deactivation after fence persistence and participant commit verifies replay and receipts without wall-clock waits.
- Persistence specs cover generated repository projection/index behavior and Project revision/receipt commits.
- Workflow specs cover failures before and after the Issue start commit, durable `EnsureStarted` replay, before-start metadata updates, during-run immutability, later-run refresh, authoritative variable precedence, run-store round trips, and built-in integration lock behavior.
- Runner unit/spec tests use fake command/filesystem boundaries to cover absent-path bootstrap, interruption before/after atomic rename, marker/origin mismatch, URL-normalizer conformance vectors, symlink/outside-root paths, review and cleanup validation, inaccessible repositories, and equal pull-request numbers in separate workspaces.
- CLI specs use existing HTTP/process/filesystem fakes for `--repo`, filtering/rendering, old-option rejection, and repository-in-use output.

**Alternative considered:** broad tests using real Git repositories or temporary host directories are prohibited by `design/testing.md` and would obscure which authority boundary failed. Deterministic fakes and grain probes exercise the races directly.

## Risks / Trade-offs

- `[Coordinator availability]` -> An unresolved participant call blocks later repository-sensitive commands for that Project. Keep the durable fence, replay lazily on every call/activation, expose the underlying failure, and never expire it by time because safety is more important than speculative progress.
- `[Coordination complexity]` -> Revisions and receipts add state to two aggregates. Limit them to narrow participant interfaces, one pending command, and architecture tests; fault-injection specs cover every uncertain-outcome boundary.
- `[Architecture expansion]` -> A durable synchronous process manager is not yet named by the architecture rules. Record its narrow downstream role before implementation and prohibit participant callbacks, duplicate business facts, and commands that write more than one domain aggregate.
- `[Project-level serialization]` -> Repository-sensitive commands in one Project are serialized even when they name different repositories. These commands are low-volume; move to per-repository coordinators only if measured contention justifies the more complex cross-repository reassignment protocol.
- `[Run snapshot staleness]` -> Repository metadata edits do not affect an active run. This is intentional for coherence; a later run resolves the updated declaration.
- `[Runner bootstrap interruption]` -> Clone or marker creation can stop before publication. Build only in the deterministic preparation directory, reject symlinks, and publish by same-parent atomic rename; retries never mutate a partially published final workspace.
- `[Runner upgrade incompatibility]` -> Old Issue-number workspaces and unversioned markers fail validation. Workspaces are rebuildable, and run-unique paths avoid mutating them; the deployment reset removes abandoned directories before the new Runner starts.
- `[Git URL equivalence]` -> Equivalent remote URLs can have different textual forms. Version the credential-free normalizer, test fixed conformance vectors in Server and Runner, and fail closed with an actionable identity error when equivalence cannot be proven.
- `[SQLite table rebuild]` -> Adding a stored generated column can rebuild `Issues`. Exercise the migration against the migrated SQLite template and take a database backup before deployment.
- `[Server/Runner skew]` -> One side may not understand the expanded identity payload or marker. Deploy Server and Runner from the same release and reject incomplete identity rather than falling back.
- `[No historical compatibility]` -> Old Issue JSON cannot reliably reconstruct target or first-start evidence. Perform the full control-plane reset below; do not run the new binary against retained pre-change Issue/Workflow or Orleans state.
- `[Rollback after Git side effects]` -> Restoring only control-plane state cannot undo pushes, merges, or pull-request changes. Permit backup restoration only before the first new dispatch; after dispatch, stop work and reconcile remotes before rollback or forward-fix instead.

## Migration Plan

1. Record the durable application process-manager rule in `design/architecture.md` and the context map, and ensure the scoped `(ProjectId, IssueNumber)` identity work is present before adding new coordinator, Workflow, or Runner contracts.
2. Add the EF migration for generated `IssueRow.RepositoryName`, its `(ProjectId, RepositoryName, Status)` index, and Project repository revision/receipt persistence. Add the coordinator state/serializers to the existing Orleans grain store.
3. Implement required Issue binding state, repository events, read DTOs/query filtering, permanent start lock, remote-alias rejection in `RepositoryPolicy`, and idempotent Issue/Project participant commands.
4. Add the Project-scoped coordinator and route Issue create/full reassignment PATCH/reopen plus repository removal through it. Add the committed-state blocker query and architecture guard against participant bypass.
5. Refactor startup to commit `IssueWorkStarted` first, then create WorkflowRun through durable input-idempotent `EnsureStarted`. Add `WorkflowRepositoryContext`, authoritative routing overlays, run-context queries, and convert workspace/rebase/cleanup routes to run-owned facts.
6. Update Runner run-path bootstrap, marker/registry validation, existing workspace-query payloads, remote identity checks, and the built-in integration lock scope. Align local and GitHub delivery inputs with the authoritative context.
7. Update the CLI command surface and rendering, then add the focused server, Runner, and CLI coverage from D8. Run the package typechecks/tests required by `AGENTS.md`.
8. Before deployment, stop Server and Runner and take separate database and Runner-root backups. Because compatibility is explicitly out of scope, reset the complete control-plane database, including product tables, events/inbox projections, profiles/sessions/locks, and Orleans grain storage; reset the Runner root as rebuildable execution state.
9. Start the new Server against the empty database so all migrations create the new schema, recreate Project/repository configuration, then start the matching Runner. Do not dispatch work until configuration and health checks pass.

Rollback before the first new dispatch consists of stopping both processes, restoring the complete pre-reset database and Runner-root backups, and starting the previous matched binaries. After any new dispatch, push, pull-request update, or merge, database restoration alone is unsafe; stop work and reconcile remote Git state before rollback, or forward-fix. The change itself performs no remote repository migration or Git rewrite.

## Open Questions

- None blocking. Whole-Project deletion and any historical Issue-state upgrader require separate product requirements rather than implicit behavior in issue 417.
