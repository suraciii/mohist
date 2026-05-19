## Context

Mohist's workflow already separates tasks that can execute agents or modify files from checks that validate results. The current check-stage review flow can still enter repeated review-fix loops because the failed review output is mostly prose, repair tasks do not receive a durable batch of failed items, and pass/fail can be inferred from artifact text instead of a declared machine-readable result. This makes it hard for users to tell whether a stage is converging.

The change should add a generic structured result and reaction model below review. Review, self-review, plan-quality checks, test failure handling, and future custom judgment tasks should all use the same workflow primitives. Mohist core must not gain review-specific objects such as `ReviewFinding`; review remains a built-in task/check implementation and default workflow configuration.

The design also needs to preserve the existing boundary from prior workflow work: tasks produce artifacts, structured outputs, repairs, and snapshots; checks remain read-only validators that parse declared outputs and decide whether a stage is blocked.

## Goals / Non-Goals

**Goals:**

- Represent structured `items[]`, verdicts, evidence, repairs, snapshots, and verification on task and check outputs using generic workflow types.
- Require AI judgment tasks to declare a structured result contract, defaulting to exactly one `<promise>PASS</promise>` or `<promise>FAIL</promise>` marker in a declared output source.
- Parse pass/fail through one shared contract instead of natural-language report interpretation.
- Treat missing, duplicate, malformed, or undeclared-source verdict markers as explicit task/check errors.
- Let task definitions declare limited in-session repair capability and record repaired item IDs, changed evidence, and verification.
- Make the built-in review task comprehensive, able to repair only safe local items, and required to produce final post-repair structured output.
- Pass failed structured context into reaction tasks so repair tasks can consume a full item batch.
- Record reaction convergence evidence: attempted, resolved, unresolved, and newly observed item IDs.
- Expose generic convergence state through stage state and issue UI without exposing review-specific lifecycle concepts as Mohist primitives.
- Keep existing review history and reviewed-snapshot behavior compatible.

**Non-Goals:**

- Do not introduce review-specific Mohist core entities such as `ReviewFinding`, `ReviewSnapshot`, or `VerificationReview`.
- Do not add a broad user-defined workflow DSL or require YAML workflow definition support in this change.
- Do not let checks start agents, modify files, or perform repair work.
- Do not allow prose-only AI reports to determine pass/fail when a result contract is declared.
- Do not suppress valid blockers or make the built-in review intentionally shallow.
- Do not replace existing review history or reviewed-SHA binding work.

## Decisions

### D1: Add Generic Workflow Result Types Below Task/Check Outputs

Extend workflow runtime types with generic structured data shared by task outputs, check outputs, and reaction task outputs:

```ts
type WorkflowVerdict = 'PASS' | 'FAIL'
type WorkflowItemSeverity = 'blocking' | 'warning' | 'follow-up' | 'info'
type WorkflowItemStatus = 'open' | 'resolved' | 'unresolved' | 'pre-existing' | 'out-of-scope'

interface WorkflowItem {
  id: string
  severity: WorkflowItemSeverity
  status?: WorkflowItemStatus
  scope?: string
  evidence: string
  suggestedAction?: string
  verification?: string
}

interface StructuredWorkflowResult {
  verdict?: WorkflowVerdict
  marker?: '<promise>PASS</promise>' | '<promise>FAIL</promise>'
  items?: WorkflowItem[]
  evidence?: string
  repairedItemIds?: string[]
  verification?: WorkflowVerification[]
  snapshot?: WorkflowSnapshot
  summary?: string
  facts?: Record<string, unknown>
}

interface ReactionTaskOutput extends StructuredWorkflowResult {
  attemptedItemIds: string[]
  resolvedItemIds: string[]
  unresolvedItemIds: string[]
  newItemIds?: string[]
}
```

Persist these fields as JSON on existing task/check current-state and execution-history records rather than creating many domain-specific tables. The stable contract is the generic shape; built-in review maps its findings into `WorkflowItem` but the storage layer does not know review semantics.

**Alternatives considered:** Adding `ReviewFinding` and review-specific tables was rejected because the product need is generic structured workflow convergence. Modeling every possible item field relationally was rejected because item schemas will evolve and custom workflows may add fields Mohist core should preserve but not interpret.

### D2: Declare Result Contracts on Task Definitions

Add an optional `resultContract` to task definitions and generated task runtime metadata:

```ts
interface ResultContract {
  kind: 'promise-marker'
  required: boolean
  outputSource: ResultOutputSource
  allowedMarkers: ['<promise>PASS</promise>', '<promise>FAIL</promise>']
  itemPolicy?: {
    blockingSeverities: WorkflowItemSeverity[]
    nonBlockingStatuses: WorkflowItemStatus[]
  }
}

type ResultOutputSource =
  | { type: 'artifact'; path: string }
  | { type: 'task-output'; key: string }
```

Built-in AI judgment tasks should use a default `promise-marker` contract. `review`, `review-self-check`, self-review, plan-quality, and similar tasks all declare where the result will be parsed from. Mohist must not scan agent logs, transcripts, or unrelated artifacts for accidental markers.

**Alternatives considered:** Scanning all output text for `<promise>` markers was rejected because it can pick up quoted examples, logs, or stale transcript text. Hardcoding parser behavior inside each check was rejected because review and self-review need identical error handling and future judgment tasks should reuse the same contract.

### D3: Centralize Strict Verdict Parsing

Create a shared parser, for example `parseStructuredResult(contract, sourceText)`, responsible for:

- Loading only the declared source.
- Finding exactly one allowed promise marker.
- Returning normalized `verdict`, `marker`, `items`, and evidence fields.
- Producing typed errors for missing, duplicate, malformed, or source-missing output.

The parser should understand the declared structured envelope around the marker. A minimal implementation can parse frontmatter, fenced JSON, or another existing artifact convention used by prompts, but the important boundary is that structured items and the marker come from the same declared source. Checks then consume the parsed result; they do not infer verdicts from prose.

**Alternatives considered:** Keeping `extractPromiseVerdict` as a loose string helper was rejected because it cannot enforce source binding, duplicate marker errors, or consistent item parsing. Treating malformed output as `FAIL` was rejected because a parser failure is an execution/check error, not a valid judgment that found blocking work.

### D4: Keep Checks Read-Only and Move Repair Policy to Tasks/Reactions

Checks should validate parsed structured results and stage policy only. They may fail or error with structured items, but they must not start agents, rewrite artifacts, or mutate the worktree. Repair work happens in either:

- the original task, if its `selfRepairPolicy` permits bounded in-session repair;
- a configured reaction task scheduled after a failed check.

Add `selfRepairPolicy` to task definitions:

```ts
interface SelfRepairPolicy {
  enabled: boolean
  allowedScopes: string[]
  maxAttempts?: number
  requiresVerification: boolean
  disallowedReasons: string[]
}
```

For the built-in review task, the prompt and task adapter use this policy to allow safe local repairs and require the final result to describe repaired item IDs, changed evidence, verification commands, and unresolved items. If the task changes the candidate, its final verdict is based on the post-repair snapshot.

**Alternatives considered:** Letting `review-passed` auto-fix findings was rejected because it violates the task/check boundary and makes a check both judge and actor. Disabling in-session repair entirely was rejected because small safe repairs are often the fastest convergent path and can reduce unnecessary reaction cycles.

### D5: Model Reaction Input as Failed Context, Not Prose Scraping

Extend reaction definitions with explicit `inputFrom` selectors that can collect failed check output, selected task outputs, artifacts, and structured item batches:

```ts
interface ReactionDefinition {
  when: ReactionTrigger
  scheduleTask: string
  inputFrom: ReactionInputSelector[]
  retryLimit: number
  afterSuccess: RecheckPolicy
  afterExhausted: BlockPolicy
}
```

When a check fails, the stage runner builds a `FailedCheckContext` containing check identity, parsed verdict, blocking items, non-blocking items, source artifact references, snapshot metadata, and relevant prior task outputs. `fix-review-findings` receives the complete blocking current-change item batch through this context instead of scraping `review.md` prose.

**Alternatives considered:** Passing only the textual check message was rejected because it preserves the current one-finding-at-a-time failure mode. Passing every artifact and transcript wholesale was rejected because it increases prompt noise and risks accidental marker parsing; reaction inputs should be explicit and bounded.

### D6: Track Convergence Attempts as Generic Stage State

Add convergence metadata to current stage state and execution history:

```ts
interface WorkflowConvergenceState {
  failedCheck?: string
  blockingItemCount: number
  directlyRepairedCount: number
  reactionAttempts: number
  attemptedItemIds: string[]
  resolvedItemIds: string[]
  unresolvedItemIds: string[]
  newBlockingItemIds: string[]
  nonBlockingItemIds: string[]
  blockedReason?: string
}
```

The state is computed from the latest task/check/reaction outputs and persisted or projected through the stage-state service. The UI renders these generic counts and item groups. It may label the built-in check as review, but the API shape remains workflow-oriented.

**Alternatives considered:** Deriving convergence state in the frontend from task messages was rejected because it would duplicate workflow policy and reintroduce prose parsing. Storing only counts was rejected because reaction tasks and rechecks need stable item IDs to verify attempted and unresolved work.

### D7: Recheck Known Items Before Allowing New Blockers to Extend the Loop

After a reaction task completes, the stage runner re-runs the configured task/check path in verification mode. For the built-in review workflow, verification mode receives known item IDs and expected repairs, then asks the review task to verify resolution first and report only policy-allowed new blockers:

- fix-introduced regressions;
- missed current acceptance-criteria blockers;
- serious safety, data, or security risks;
- unresolved known blockers.

The check still parses the final declared output and applies item policy. Non-blocking follow-ups and out-of-scope items remain visible but do not block by default.

**Alternatives considered:** Re-running a full unconstrained review after every reaction was rejected because it can keep discovering one new blocker per loop without explaining convergence. Skipping review after a reaction and trusting the repair task was rejected because a reaction cannot directly mutate a failed check into pass without evidence.

### D8: Update Built-In Prompts to Emit the Generic Contract

Review, self-review, and review-fix prompts should be updated to require a structured result section with exactly one promise marker. The review prompt should instruct the agent to continue after the first blocker, inspect acceptance criteria and affected adjacent paths, classify items into directly repaired, blocking current-change, non-blocking follow-up, and pre-existing/out-of-scope groups, and provide verification evidence.

The fix prompt should consume `FailedCheckContext`, attempt related blocking items together, and return attempted/resolved/unresolved IDs. Prompt wording should make unsafe or ambiguous repair a reported unresolved item, not a silent change.

**Alternatives considered:** Enforcing comprehensiveness only in TypeScript validation was rejected because code can validate shape but not make an agent inspect more thoroughly. Prompt-only enforcement was also rejected because Mohist still needs strict parsing and check errors for malformed output.

## Risks / Trade-offs

- [Risk] Generic item fields may be too small for some custom workflows. → Mitigation: include `facts` or extension fields that Mohist persists but does not interpret, while keeping core convergence based on stable IDs, severity, status, evidence, and verification.
- [Risk] Strict marker parsing may initially fail existing prompts more often. → Mitigation: update built-in prompts and tests in the same change, surface clear parser errors, and keep malformed output as retryable task/check errors.
- [Risk] In-session review repair can hide important changes if prompts are too permissive. → Mitigation: encode conservative repair boundaries, require repaired item IDs and verification, and fail or report unresolved when verification is missing.
- [Risk] Stable item IDs may be hard for agents to maintain across rechecks. → Mitigation: assign IDs in the initial structured output and pass them back in verification mode; allow new IDs only for policy-allowed new blockers.
- [Risk] Persisting structured output in multiple existing stores can drift. → Mitigation: centralize normalization and persistence through the task/check result write path and have stage-state project from the latest authoritative structured result.
- [Risk] UI could become review-specific despite generic APIs. → Mitigation: render generic convergence fields first and keep review-specific wording limited to built-in task labels.

## Migration Plan

1. Add TypeScript types for `WorkflowItem`, `StructuredWorkflowResult`, `ResultContract`, `SelfRepairPolicy`, `FailedCheckContext`, `ReactionTaskOutput`, and `WorkflowConvergenceState`.
2. Extend task/check result persistence and stage-state projection to store and return structured result JSON without deleting existing output fields.
3. Implement the shared strict promise-marker parser bound to declared output sources, with typed errors for missing, duplicate, malformed, or unavailable sources.
4. Add default result contracts to built-in review, self-review, and other AI judgment tasks that currently rely on promise markers.
5. Update read-only checks such as `review-passed` and `self-review-passed` to use the shared parser and item policy instead of prose interpretation.
6. Add task `selfRepairPolicy` support and update the built-in review task/prompt to perform only bounded safe repairs, record repaired IDs and verification, and produce a post-repair final verdict.
7. Add reaction input assembly for failed checks and wire `fix-review-findings` to consume the full structured blocking item batch.
8. Persist reaction task attempted/resolved/unresolved IDs and update the stage runner recheck path to run verification mode after repair.
9. Expose convergence state through stage-state or issue detail APIs and update the UI to show failed check, item counts, direct repairs, reaction attempts, resolved/unresolved counts, blocked reason, and non-blocking follow-ups.
10. Add regression tests for parser error cases, source-bound parsing, structured item persistence, review/self-review shared parsing, direct repair recording, reaction batching, recheck convergence, and UI/API convergence projection.

Rollback strategy: the changes are additive to output JSON and stage-state projections. If rollout fails, disable the new result contracts for built-in tasks and return checks to the previous parser path while leaving persisted structured fields unused. Avoid destructive schema migrations so older execution history and review artifacts remain readable.

## Open Questions

- Which artifact envelope should be standardized for structured results: fenced JSON, frontmatter, or an existing Mohist artifact metadata block?
- Should parser errors automatically retry the producing task once, or should they immediately block the stage with a clear malformed-output error?
- What retry limit should the default review reaction use before surfacing an exhausted convergence state to the user?
