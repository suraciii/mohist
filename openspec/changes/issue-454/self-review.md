## Findings

### 1. High: The plan fabricates a comment author from a nonexistent authenticated actor

The issue requires comment rows to show their author, and the history spec describes a recorded author with an unavailable-author fallback. The design instead states that the API has one authenticated operator principal and proposes projecting every comment author as `Operator` without storing or receiving actor identity ([design.md](./design.md#7-expose-role-level-comment-attribution-without-inventing-users)); T-004 locks in the same behavior ([tasks.json](./tasks.json)). That premise is not supported by the current comment path: `AddCommentRequest`, `IssueGrain.AddCommentAsync`, `IssueCommentRow`, and `IssueCommentDto` contain no actor, while `OperatorCredential` is used by the dead-letter routes rather than as general Issue-route authentication. CLI and automated callers can also create comments through the same actor-free endpoint.

As written, `Operator` would be a guessed role, not the author of the comment, so the plan does not satisfy the user-visible attribution requirement or its own goal of truthful attribution. The proposal/spec/design must agree on an actual source of author identity and its behavior for existing comments. If adding identity is outside this Web-only issue, the product requirement must be narrowed explicitly rather than labeling all comments with an unproven author.

### 2. High: Frontmatter metadata can contradict authoritative Issue fields

The design says `risk` and workflow profile already have authoritative fields outside the body, but then directs the detail page to display parsed body metadata in Details ([design.md](./design.md#4-move-issue-body-partitioning-into-entitiesissue)); the metadata spec and T-001 likewise do not define precedence or labels when values differ. This conflict is reachable today: `CreateIssueDialog` submits the original template body unchanged while a user-touched risk or workflow selection is submitted separately as the authoritative `risk` or `workflowProfileId`. A body containing `risk: medium` can therefore create an Issue whose actual risk is `high`, after which the planned Details row would display stale `medium`. A recommended workflow may also differ intentionally from the selected workflow.

That would replace duplicate facts with contradictory facts. The plan must define one display authority for each concept and distinguish recommendation metadata from current Issue state. Specs and T-001 need explicit conflict scenarios, including overridden risk and recommended-versus-selected workflow, before implementation can proceed.

### 3. Medium: Unclosed frontmatter is outside the sanitization contract

The issue acceptance criterion says the rendered description never contains frontmatter and the edit dialog never presents raw frontmatter. The metadata spec limits its operative scenarios to well-formed or recognized leading frontmatter, while the design only guarantees removal for a bounded leading envelope and T-001 tests only a bounded malformed envelope. The existing parser classifies a missing closing delimiter as malformed, so an issue body beginning with `---` and no closing delimiter has no specified description, preview, Details, or edit behavior.

The plan must either define deterministic handling for an unclosed leading envelope and add matching scenarios/acceptance coverage, or explicitly narrow the product criterion to bounded frontmatter. Leaving the case undefined permits the original giant-heading/raw-editor defect to remain for malformed stored bodies.

## Review Summary

The workflow, Changes/Artifacts, responsive-stage, Activity-subject, fragment-navigation, and task-DAG portions are internally coherent. The author source and metadata authority defects are blocking because they can produce false user-visible facts; the malformed-frontmatter gap also needs a contract decision.

<promise>FAIL</promise>
