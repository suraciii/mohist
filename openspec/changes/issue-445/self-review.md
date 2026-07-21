## Findings

### High: The retained `rawWith` channel contradicts the normative single-input contract

The Action input spec requires every built-in to derive all Action-owned behavior exclusively from the rendered and manifest-validated `with` payload (`specs/action-input-sourcing/spec.md:3`). The design nevertheless permits `rawWith` as an unrendered invocation field (`design.md:38`), and T-001 explicitly requires `mohist/openspec-tasks` to keep using it to propagate nested task templates (`tasks.json:24`). That raw subtree can differ from validated `with` and directly changes generated tasks, so the planned implementation does not satisfy the written requirement even though both representations originated in the workflow definition.

Before build, the artifacts must choose one contract: either narrow the spec to prohibit only post-render Variable reads and explicitly define the constrained `rawWith` exception, or redesign task-template preservation so the Action consumes one validated template-preserving input representation. Leaving the conflict unresolved makes both implementation and acceptance ambiguous.

### High: Archive retry recovery cannot identify the chosen collision destination

The design removes persisted `openspecArchiveName` state and says a retry will infer the destination from archive filesystem state (`design.md:75`). It also requires collision suffixes and date rollover to work, while ambiguity must fail (`design.md:96`, `tasks.json:14`). These rules do not identify the current attempt when an older `DATE-issue-N` archive already exists, the current attempt moves to `DATE-issue-N-v2`, and the process fails before commit: on retry both directories legitimately match, and the proposed state contains no discriminator that proves `-v2` belongs to this attempt.

The design must specify a deterministic, testable ownership signal for the selected destination that is not a hidden Run Variable input, including behavior after rename and before staging/commit. It must also require fake time or an injected clock for date-rollover coverage, consistent with the repository's prohibition on real-time-dependent tests. Without this, the existing retry recovery can become permanently stuck or select the wrong archive.

### Medium: Migration order conflicts with the executable task graph

The design migration plan says to remove Variables from the invocation context first (`design.md:102`), before migrating handlers (`design.md:103`). The task graph instead makes boundary enforcement T-004 depend on all concrete migrations (`tasks.json:97`), which is necessary because current built-ins still read `context.variables`. An autonomous builder cannot follow both sequences, and following the design order would require temporary compatibility access or leave the Runner uncompilable.

Align the design migration plan with the task DAG: migrate all concrete readers first, then narrow the invocation type/runtime object and run the boundary audit.

<promise>FAIL</promise>
