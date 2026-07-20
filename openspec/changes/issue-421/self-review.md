## Findings

### 1. High: The proposed `Runner -> Issue` dependency is rejected by the architecture suite

Design D1 requires `WorkflowItemTranslator` in `Mohist.Server.Runner.Services` to call `IssueQuerier` in `Mohist.Server.Issue.Services` (`design.md:29-31`), and T-001 plans that same direct path (`tasks.json:9-17`). The enforced domain allowlist permits `Runner -> Sessions` and `Runner -> Workflow`, but not `Runner -> Issue` (`packages/server/tests/Mohist.Server.ArchTests/ArchitectureRules.cs:383-416`); `DomainModules_ShouldNotDependOnEachOther` rejects every unlisted direction (`ArchitectureRules.cs:418-439`). Implementing the plan as written therefore cannot satisfy T-001's own `npm test` acceptance criterion.

The plan must choose an architecture-compliant assembly boundary outside the Runner and Issue domain namespaces, or explicitly change the canonical architecture and its allowlist with a justified new domain relationship. Moving only the value type does not solve the dependency: placing it under Issue leaves `Runner -> Issue`, while placing it under Runner makes the Issue-side producer depend on Runner.

### 2. Medium: Missing-parent failure is extra behavior and its dispatch outcome is incomplete

The issue and capability spec define context for issues that currently have a parent and require lifecycle/stage behavior to remain unchanged (`specs/sub-issue-plan-context/spec.md:38-55`). They do not define corruption handling. Design D1 nevertheless requires a missing or malformed referenced parent to fail dispatch (`design.md:39`), and T-001 makes that observable failure and its tests mandatory (`tasks.json:12,17`). This invents behavior outside the normative contract instead of deriving it from a requirement.

The proposed failure is also underspecified against the current dispatch pipeline. `DispatchService` permanently rejects a claimed task only for `WorkflowDispatchRejectedException` (`packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:183-187,219-223`). Query, missing-row, or deserialization exceptions fall into the generic catch and leave the task Running for poll redelivery (`DispatchService.cs:188-195,224-231`). As written, the design's "fails with an actionable consistency error" can become indefinite retries. The plan must either remove this unrequested policy or specify its normative behavior, exception conversion, ownership, and testable terminal/retry semantics.

### 3. Low: The task applies Orleans field-id semantics to the HTTP DTO

T-001 says both `WorkDispatch` and `WorkDispatchResponse` carry `ParentIssueContext` "using a new Orleans field id" (`tasks.json:13`). Only `WorkDispatch` is an Orleans `[GenerateSerializer]` contract and needs the next appended id (`packages/server/src/Mohist.Server/Runner/Grains/IRunnerGrain.cs:87-131`). `WorkDispatchResponse` is a plain HTTP record with no Orleans field ids (`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:656-682`); it needs only an additive property and explicit mapping in the poll route. The task wording must distinguish these contracts so implementation does not add meaningless serialization annotations or mis-handle positional DTO mapping.

## Verified Coverage

Apart from the findings above, the proposal names exactly one capability and the matching spec exists. Parent title/body inclusion, child-scope authority, parent comment/artifact exclusion, sibling exclusion, ordinary Plan preservation, and non-Plan/lifecycle isolation trace consistently from the issue through the proposal, spec, design, and T-001. `tasks.json` parses, contains all required fields, and its single-task dependency graph is a valid DAG.

## Conclusion

The product scope and capability coverage are coherent, but the chosen implementation boundary fails an enforced architecture rule and the added corruption policy is neither normatively grounded nor operationally complete. The plan is not ready for autonomous build execution.

<promise>FAIL</promise>
