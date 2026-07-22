# Review Findings

## 1. The centralized descriptor catalog does not describe the actual resource leaves

**Severity: blocking**

`packages/cli/Mohist.Cli/ResourceOutput.cs:18-57` uses one `CommonFields` list for most table shapes instead of declaring each leaf's actual fields. This rejects valid fields such as `key` on `label list`, repository-specific fields such as `gitUrl` and `baseBranch`, and the fields exposed by several agent/session and template resources. The bare `--json` result consequently advertises an incomplete contract, while valid resource data cannot be selected through the documented interface.

The catalog also classifies `AgentSessionList` and other collection leaves as `Single` because they are absent from the collection switch (`ResourceOutput.cs:26-45`). A selected request against one of those leaves attempts to project an array as an object and returns `invalid-response` instead of the required JSON array. The issue requires every resource leaf to have an exact descriptor and stable cardinality, so a generic fallback catalog is not sufficient.

## 2. Described workflow-profile listing bypasses local JSON discovery

**Severity: blocking**

In `MohistCliCommands.ProjectWorkflow.cs:128-145`, the `--described` branch resolves a Project and calls `PrintWorkflowProfilesDescribedAsync` before it reads or handles the `output` value. Invoking `project workflow profile list --described --json` therefore contacts the Server and emits human text instead of returning the descriptor fields locally; it can also fail on missing Project/Server context. This violates the bare-`--json` requirement that discovery perform no Project resolution or remote request, and the selected-field requirement for every resource-returning leaf. `MohistCliApi.PrintWorkflowProfilesDescribedAsync` (`MohistCliApi.cs:1070-1090`) has no selection or machine-output path to compensate.

## 3. `mo skills` still exposes boolean JSON modes

**Severity: blocking**

`MohistCliCommands.Skills.cs:77`, `103`, and `185` still register `Option<bool>("--json")` and directly serialize the complete skill object/list. These leaves do not accept comma-separated fields, do not support local bare-`--json` discovery, and do not reject duplicate/unknown selections. They are resource-returning leaf commands in the current command tree, so they remain outside the shared `--json [fields]` contract despite the migration of the Mohist resource commands.

## 4. The CLI test suite is still failing after the remediation

**Severity: blocking**

`dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` currently reports `213` failures out of `1,399`. The failures include existing tests still invoking removed `--output`/`-o` and `--project-id` surfaces, tests expecting envelope JSON, and tests expecting exit code `4`. These are not updated contract coverage: they leave the repository's required CLI suite red and do not verify the new behavior across the affected command families. The task acceptance explicitly requires the CLI test suite to pass, so the change is not ready to merge until the tests are migrated or replaced with equivalent contract assertions.

<promise>FAIL</promise>
