## Context

The two built-in system workflow profiles resolve their user-facing description from **two different sources**, and the sources have already drifted:

- `mohist/local` resolves `Description` at runtime from the parsed YAML (`MohistLocalIssueWorkflowProfile.ResolveDescription()` → `MohistWorkflow.Definition.Description`), with a `"No description provided"` empty fallback. YAML is the single source of truth.
- `mohist/github-pr` resolves `Description` from a C# `public const string GithubPrDescription` compiled into the binary (`MohistGithubPrIssueWorkflowProfile.cs:10`). The `description` parsed from `mohist-github-pr.workflow.yaml` into `MohistWorkflow.GithubPrWorkflowDefinition.Description` is **never read** — dead code. Any copy edit to the github-pr description forces a rebuild, and editors must remember which profile reads from where.

The two copies have drifted in wording (the const says "auditability on GitHub matters"; the YAML says "traceable GitHub PR record per issue").

A second call site, `ProjectWorkflowProfileManager.BuildSystemTemplates()` (`ProjectWorkflowProfileManager.cs:31`), assembles the system-template catalog used by `ListSystemTemplatesAsync` / `GetSystemTemplateInfo`. It already reads the **local** description from `MohistWorkflow.Definition.Description` but the **github-pr** description from the same deleted-bound `GithubPrDescription` const (with an extra `.TrimEnd()` the local branch does not apply). So the same divergence is duplicated there.

**Constraints / facts that make this low-risk:**

- `WorkflowYamlSerializer.FromYaml` already parses `description` into `WorkflowDefinition.Description` (`WorkflowYamlSerializer.cs:28`, via `NullIfEmpty`), and `mohist-github-pr.workflow.yaml:1` already carries the authoritative description including the `gh` / `gh auth login` prerequisite and "GitHub PR" wording that downstream specs assert on. No parsing-logic change is needed (Non-Goal).
- `MohistWorkflow.GithubPrWorkflowDefinition` is a cached `Lazy<WorkflowDefinition>` singleton already referenced by the profile's `Definition` override — so reading `.Description` off it adds no new I/O or allocation.
- `MohistLocalIssueWorkflowProfile.ResolveDescription()` (`MohistLocalIssueWorkflowProfile.cs:28`) is the proven reference pattern to mirror.

**Stakeholders:** Server only. No runner / web / CLI / API contract / persistence / parsing change. The existing `MohistGithubPrIssueWorkflowProfileSpecs` and `ProjectWorkflowProfileManager` spec cases are the guardians.

## Goals / Non-Goals

**Goals:**
- Make `MohistGithubPrIssueWorkflowProfile.Description` resolve from `MohistWorkflow.GithubPrWorkflowDefinition.Description` (the parsed YAML) with the same empty fallback as the local profile.
- Delete the `public const string GithubPrDescription` and the `.TrimEnd()` call that referenced it.
- Make `ProjectWorkflowProfileManager.BuildSystemTemplates()` assemble both templates' descriptions from the parsed `WorkflowDefinition.Description` with the identical pattern (drop the const reference and the github-pr-only `TrimEnd`).
- Rewrite the description specs that asserted against the `GithubPrDescription` const symbol to assert the YAML-sourced description (profile `Description` + `SystemTemplateInfo.Description`).

**Non-Goals:**
- No change to the `description` wording / positioning in the YAML itself (the "not suited for refactor" framing is tracked separately). The displayed github-pr text *does* change from the stale const to the YAML — that retirement is the point of the change, not a side effect.
- No change to `WorkflowYamlSerializer` parsing logic.
- No API, schema, runner, web, or CLI change.

## Decisions

### D1. Mirror `MohistLocalIssueWorkflowProfile.ResolveDescription()` on the github-pr profile

Replace the const-backed property with a private static helper that reads `MohistWorkflow.GithubPrWorkflowDefinition.Description`:

```csharp
public override string Description => ResolveDescription();

private static string ResolveDescription()
{
    var description = MohistWorkflow.GithubPrWorkflowDefinition.Description;
    return string.IsNullOrWhiteSpace(description) ? "No description provided" : description;
}
```

This is a structural twin of the local profile's helper (same name, same shape, same fallback string), so the two profiles "read the description identically" — an explicit acceptance criterion.

**Alternatives considered:**
- *Hoist a shared `protected static string ResolveDescription(WorkflowDefinition def)` into `MohistIssueWorkflowProfileBase` and call it from both profiles (DRY).* Rejected: it would touch the local profile (wider blast radius) for a 2-line duplication; keeping the change localized to the github-pr profile matches the proposal's "single subsystem" framing and leaves the proven local path untouched. The duplication is two near-identical 3-line methods, which is acceptable symmetry.
- *Inline the ternary directly in the property getter.* Rejected: local uses a named method; matching it keeps the profiles structurally symmetric and makes the "identical resolution pattern" claim visually verifiable in review.

### D2. Unify `BuildSystemTemplates()` to read `Definition.Description` for both branches

In `BuildSystemTemplates()` (`ProjectWorkflowProfileManager.cs:31`), the github-pr branch changes from:

```csharp
var githubPrDescription = string.IsNullOrWhiteSpace(MohistGithubPrIssueWorkflowProfile.GithubPrDescription)
    ? "No description provided"
    : MohistGithubPrIssueWorkflowProfile.GithubPrDescription.TrimEnd();
```

to read the parsed YAML the same way the local branch already does:

```csharp
var githubPrDefinition = MohistWorkflow.GithubPrWorkflowDefinition;
var githubPrDescription = string.IsNullOrWhiteSpace(githubPrDefinition.Description)
    ? "No description provided"
    : githubPrDefinition.Description!;
```

The `.TrimEnd()` is dropped so the github-pr branch is byte-for-byte identical in shape to the local branch (which never trimmed). The local branch is left unchanged.

**Note on the dropped `TrimEnd()`:** the const path trimmed trailing whitespace because the raw-string literal `"""..."""` could carry a trailing newline. The YAML path does not need it: `WorkflowYamlSerializer` applies `NullIfEmpty` and the block-scalar `|` parsing already normalizes the value, and the local branch proves the un-trimmed value renders correctly. Dropping it is required to satisfy "both branches assemble identically".

**Alternatives considered:**
- *Keep `TrimEnd()` on the github-pr branch for parity with the current const behavior.* Rejected: it reintroduces an asymmetry between the two branches (local never trimmed), which is exactly the divergence this change removes. The YAML-sourced value needs no trimming.
- *Route both branches through the profiles' `Description` property instead of re-reading `WorkflowDefinition.Description`.* Rejected: `BuildSystemTemplates` is a `static` field initializer (`SystemTemplates = BuildSystemTemplates()`) and constructs no profile instance; introducing profile instantiation here would add DI coupling to a static path that deliberately avoids it. Re-reading the cached `Lazy<WorkflowDefinition>.Description` is the same data the profile resolves, with no extra cost.

### D3. Rewrite the `_AsConstant` spec to assert the YAML source

`MohistGithubPrIssueWorkflowProfile_DescriptionSurfacesGhCliPrerequisite_AsConstant` (`MohistPrIssueWorkflowProfileSpecs.cs:63`) asserts the const symbol directly; it must be removed (the symbol is gone).

- **Removed:** the `_AsConstant` case — it references a deleted symbol.
- **Kept unchanged:** `..._DescriptionSurfacesGhCliPrerequisite` (asserts `gh`, `gh auth login`, `GitHub PR` on `profile.Description`) — still passes because the YAML carries those tokens; it now additionally proves the YAML is the live source. `..._ExposeCorrectMetadata` (non-blank `Description`) and the `GetSystemTemplateInfo_GithubPr` / `SystemTemplates_ExposeGithubPrTemplate` cases (assert `gh auth login` on `SystemTemplateInfo.Description`) likewise stay green.
- **Added:** a focused assertion that `profile.Description` equals `MohistWorkflow.GithubPrWorkflowDefinition.Description` (the parsed YAML), and that `SystemTemplateInfo.Description` for github-pr comes from the same source — directly encoding the "single source of truth" requirement.

**Alternatives considered:**
- *Keep a renamed `_AsConstant`-style case asserting the same substrings on `profile.Description`.* Rejected: it would duplicate `..._DescriptionSurfacesGhCliPrerequisite`. The substring case already covers the token presence; the new equality assertion covers the source-of-truth contract. No redundancy.

## Risks / Trade-offs

- **[User-visible description text changes wording]** → Mitigation: this is intentional — the const was the stale copy ("auditability on GitHub matters") and the YAML is the intended authoritative text. Both copies contain the tokens (`gh`, `gh auth login`, `GitHub PR`) downstream consumers rely on, so no integration breaks. The Non-Goal only protects the YAML's *positioning* language, which is untouched.
- **[Dropping `TrimEnd()` surfaces trailing whitespace in the github-pr description]** → Mitigation: the local branch has never trimmed and renders correctly; `WorkflowYamlSerializer` + block-scalar parsing normalizes the value. The existing `SystemTemplates_ExposeGithubPrTemplate` spec (which asserts a substring) and the new source-equality assertion would catch any regression.
- **[A stale reference to `GithubPrDescription` survives elsewhere]** → Mitigation: `TreatWarningsAsErrors` + the compile gate turn any dangling reference into a build failure; a repo-wide grep (`GithubPrDescription`) confirms the only references are the profile, `BuildSystemTemplates`, and the one spec (all rewritten here).
- **[DRY: two near-identical `ResolveDescription()` methods now exist]** → Trade-off accepted: the duplication is 3 lines × 2 profiles and is what makes "both profiles read identically" structurally self-evident. Centralizing it in the base class would widen the blast radius onto the proven local path for negligible gain.

## Migration Plan

Server-only refactor — no API, wire, persistence, or deploy change; no data migration. Rollout is by commit ordering within the PR, each step compiled + spec-green before the next:

1. **Profile + manager together** — delete the `GithubPrDescription` const; add `ResolveDescription()` to `MohistGithubPrIssueWorkflowProfile`; flip its `Description` to call it; rewrite the github-pr branch of `BuildSystemTemplates()`. These must land in one commit because the const is referenced from both sites — deleting it without updating both is a compile break. Run `dotnet build Mohist.sln`.
2. **Spec rewrite** — remove the `_AsConstant` case; add the source-equality assertions. Run the `MohistPrIssueWorkflowProfileSpecs` suite.
3. **Verify acceptance** — `npm test` (C# `TreatWarningsAsErrors` acts as lint; the profile + manager spec suites confirm both description surfaces resolve from the YAML).

**Rollback:** revert the PR. No persistent state references the description source, and no external contract changed, so either direction is transparent. The const can be restored verbatim if needed.

## Open Questions

- None material. The YAML description already exists and is non-blank, the reference pattern is proven, and the spec guardians already assert the tokens that must survive. The only judgment call — accepting 3-line duplication over a base-class helper — is resolved in D1 by the proposal's "single subsystem" framing.
