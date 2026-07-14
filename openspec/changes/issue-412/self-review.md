# Self Review Report

## Result: FAIL

## Repaired Items

_None._ The single substantive issue found crosses a product invariant stated in the
issue body and is therefore not safely repairable under the repair policy ("Do not make
broad product or architectural changes during self-review"). It is recorded below as a
blocking item. The remaining findings are minor/additive and listed as follow-ups.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: consistency / alignment
  Evidence: The issue body's **Domain Model** states an explicit invariant:

  > 谱系属性以生产时聚合自身已知的信息为限，**不允许为印制属性发起跨聚合查询**。
  > ("lineage attributes are limited to what the aggregate already knows at production
  > time; cross-aggregate queries are not permitted for stamping attributes.")

  This invariant is reified as an absolute rule in two plan artifacts:

  - `proposal.md` Capability `event-lineage-stamping`: "Producers **SHALL NOT** issue
    cross-aggregate queries to stamp lineage."
  - `specs/event-lineage-stamping/spec.md` requirement "Stamping uses only identity the
    aggregate already holds": "Producers **SHALL NOT** issue cross-aggregate queries to
    gather lineage for stamping. Lineage **SHALL** be derived solely from the producing
    aggregate's own state or from annotations/labels already attached to it."

  Yet `design.md` D5 and task **T-003** deliberately introduce exactly such a
  cross-aggregate read: `IssueStore.SaveAsync` resolves `epicid` via an indexed lookup
  against the `EpicIssue` join table (`db.EpicIssues`, by `ProjectId`+`IssueId`). D5
  itself calls this "the single, narrow exception to the 'no cross-aggregate queries'
  rule." Verified against the codebase: `IssueStore.cs` today stamps only from its own
  state (`issueno`) and performs no `EpicIssue` read; the `EpicIssue` table is owned by
  `EpicGrain` (15+ `db.EpicIssues` sites). So T-003 would add the first cross-aggregate
  stamping read in the system.

  The consequence is a testable inconsistency: a spec test written from the requirement
  text ("does not issue cross-aggregate queries") fails against the T-003 implementation,
  and the implementation contradicts both the spec and the issue's stated invariant.

  This is **not safely repairable in either direction** during self-review:
  - Editing the spec/proposal to bless the exception overrides a user-voice invariant —
    a product/architectural change the repair policy forbids here.
  - Editing the design to remove the exception breaks acceptance criterion AC2
    ("查看归属于某 epic 的 issue 的事件，能看到 epic 标识"), because the Issue aggregate
    does not own epic membership and `epicid` is otherwise unavailable at emit time.

  SuggestedAction: The product owner resolves the inherent tension between AC2 (epicid
  required on `issue.*` events) and the "no cross-aggregate stamping queries" invariant.
  Recommended options:
  - (a) **Explicitly bless the D5 bounded exception** — update the issue body, the
    proposal Capability, and the spec requirement to document it as the single permitted
    exception (transaction-scoped `EpicIssue` join-row read; no grain call, no Epic
    domain logic), plus a spec scenario asserting that scope.
  - (b) **Honor the invariant via denormalization** — write `epicid` onto Issue state
    from `EpicGrain` on link/unlink (EpicGrain already owns the `EpicIssue` link);
    `IssueStore` then stamps from own state. Update design D5, T-003, and the migration
    plan.
  - (c) **Drop `epicid` from `issue.*` events** and document the gap vs AC2 (least
    recommended; breaks epic-level subscription before it starts).
  Status: unresolved

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency / completeness
  Evidence: `design.md` D2 and task **T-002** commit to stamping `stage` on
  `FeedbackRequested` (type `com.mohist.workflow.feedback.requested`) via structural
  inspection, because the event record is stage-bearing. But the stage matrix in both
  `design/event-protocol.md:69-70` and `specs/event-lineage-stamping/spec.md` lists only
  `workflow.stage.*` / `task.*` / `check.*` for the `stage` attribute, and T-001's
  catalog declaration groups only those families as stage-required. So `feedback.requested`
  stamps `stage` (T-002 AC) without the spec or catalog requiring or even mentioning it,
  and design **OQ2** still marks it unconfirmed ("confirm this is desired"). The behavior
  is additive (no conformance failure), but a future developer could drop the stamp with
  no test catching it.
  SuggestedAction: Resolve design OQ2. If confirmed, add `workflow.feedback.requested` to
  the spec's stage list and consider declaring `stage` required for it in the catalog; if
  not, remove the `FeedbackRequested` clause from T-002's acceptance criteria.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: dependencies
  Evidence: T-007 ("Reconcile issueno/epicno readers") declares `dependsOn: ["T-003"]`.
  Its acceptance criterion "No producer or consumer references `issueno` as the primary
  key or references `epicno` anywhere" is a cross-task end-state invariant whose producer
  side is owned by T-003 (`issueno`) and T-004 (`epicno`). T-007 only needs T-003 for its
  own reader edits, and tasks execute in priority order so T-004 precedes T-007 in
  practice, so this is functionally safe. Noted only because the global AC spans tasks not
  present in `dependsOn`.
  SuggestedAction: Optionally restate T-007's global AC as scoped to its own consumer
  edits, or add a cross-task final-verification note. No dependency change required.
  Status: follow-up

<promise>FAIL</promise>
