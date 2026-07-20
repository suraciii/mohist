## Findings

### 1. High: Excluding archived children contradicts the required "every current child" view and existing archive cascade

The composite detail spec requires the parent page to list "every current child" and says a child disappears when it is detached (`specs/composite-issue-detail/spec.md`, requirements "Parent details list every current child" and "Composite detail data reflects current relationships"). Archiving does not remove `ParentIssueNumber`; existing composite lifecycle archives children together with their parent, so archived children remain current relationship members and are needed when viewing an archived parent.

The design instead says archived children are excluded from child collections and counts (`design.md`, Decision 1), and T-001 makes that exclusion an acceptance criterion (`tasks.json`, T-001). That would make an archived parent appear to have no children, suppress the composite detail mode, and expose workflow-oriented UI for an object that is still a parent. The plan must choose relationship semantics consistently: current child rows/totals should be based on `ParentIssueNumber` regardless of archive state, or the specs must explicitly define a separate archived-parent behavior and provide a usable archived child view.

### 2. High: The proposed parent-candidate data cannot enforce the spec's eligibility rule

The creation spec requires the selector to exclude issues that are not eligible parents, including issues "otherwise unavailable as a parent" (`specs/issue-creation-assignment/spec.md`, requirement "New Issue selects an eligible parent"). Current server eligibility rejects a parent when `Status != Backlog` **or** `HasWorkflowStarted`; a reopened issue can be Backlog while retaining `HasWorkflowStarted`, so status and `parentIssueRef` are insufficient to determine eligibility.

The design proposes deriving candidates from the active issue list and applying only read-model indicators (`design.md`, Decision 4), while T-004 explicitly claims the existing list exposes enough data and depends on no server work (`tasks.json`, T-004 notes). `IssueReadModel` does not expose `HasWorkflowStarted` or a parent-eligibility projection. As written, the picker will offer some server-known ineligible issues and violate the normative selector requirement before the POST race case even applies.

The plan must add a server-owned eligibility signal/candidate query, or add the minimal durable fact needed to the issue read contract and make T-004 depend on that output. POST validation should remain authoritative for races, but it cannot substitute for the selector's required candidate filtering.

### 3. Medium: T-004 is declared independent while its own usable-delivery criterion consumes T-001/T-003 output

T-004 says successful creation must let the parent detail show the new child without a reload, but it has no dependency. That observable result requires T-001's child projection and T-003's parent detail UI. Before those tasks, broad `['issues']` invalidation can refresh existing count metadata but cannot render the specified child list.

Either narrow T-004's acceptance to the independently deliverable creation flow and cache invalidation contract, or add dependencies on the tasks whose output it explicitly consumes. The task graph currently overstates T-004's independent usability.

## Conclusion

The proposal and capability boundaries match issue 420, but the archive semantics and parent eligibility contract are unresolved implementation blockers, and the task DAG contains an overstated independent delivery. These must be corrected before autonomous build execution.

<promise>FAIL</promise>
