# Review Findings

## F-01 (HIGH): Authoritative routing overlay still contaminates `vars`

**Location:** `packages/server/src/Mohist.Server/Workflow/Services/AuthoritativeRoutingOverlay.cs:35-76`, applied by `WorkflowProfileManager.ResolveEffectiveVariableBundleAsync` at `:246-253`.

The change removes the runtime roots from `IssueVariableBuilder`, but the effective-variable resolution path still applies `AuthoritativeRoutingOverlay`. That overlay writes `mohist`, `project`, `issue`, `repository`, and `workspace` into the `VariableBundle.Vars`; its workspace value still includes `changeDir`. `BuildPayloadAsync` then assigns that bundle directly to the dispatch payload's `vars` property. As a result, a normal dispatch still exposes `${{ vars.project.id }}`, `${{ vars.mohist.runId }}`, and `${{ vars.workspace.changeDir }}`, violating the requirement that `vars` contain only merged user Variables and the closed-namespace requirement. The same contamination is returned by effective-variable/display paths.

Preserve the overlay's routing-authority behavior, but apply it to the dedicated runtime payload roots (`issue`, `repository`, and `workspace`) rather than writing runtime facts into `vars`; remove the off-table `mohist`/`project` entries and `workspace.changeDir`. Add a regression assertion that both `vars` and the effective-variable API contain no runtime-context copies.

## F-02 (HIGH): Preview rendering accepts arbitrary roots instead of the closed root set

**Location:** `packages/server/src/Mohist.Server/Workflow/Services/Prompts/PromptTemplateEngine.cs:98-113`.

`TryResolve` resolves any property present at the top level of the caller-provided JSON context. There is no allowlist for the ten public roots, so the preview and project-template preview endpoints still successfully render `${{ project.id }}`, `${{ mohist.runId }}`, `${{ workspace.changeDir }}`, or a bare `${{ foo }}` when those properties are supplied in preview context. This diverges from execution, where the runner assembles only the documented roots, and violates the acceptance criteria that off-table roots are not parsed and user Variables are reachable only through `vars.*`.

Make the shared preview resolution reject paths whose first segment is outside `workflow`, `stage`, `work`, `issue`, `repository`, `workspace`, `vars`, `tasks`, `prompts`, and `failure` (and ensure bare user-variable paths therefore fail). Add preview behavior-vector coverage for an off-table root and for a bare Variable key, alongside the existing missing-reference assertions.

<promise>FAIL</promise>
