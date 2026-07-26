# Review: Issue 482

## Findings

### P1: Nested group help advertises command paths that do not parse

`CommandHelpRenderer.RenderGroup` builds `USAGE` and `FURTHER HELP` from only
`group.Name` at [CommandHelpRenderer.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandHelpRenderer.cs:66). As a result, `mo agent model --help` says `mo model ...` and `mo project workflow prompt --help` says `mo prompt ...`; neither is a valid root command. This violates the leaf/group help requirement to enable an exact invocation. Build the complete ancestor path for group rendering, as the leaf and parse-error paths already do, and add coverage for nested group help.

### P1: Leaf help labels optional options as required

`FormatSymbol` treats every value-taking `Option` as required by inspecting its
arity at [CommandHelpRenderer.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandHelpRenderer.cs:235). For example, `mo label update --help` labels optional `--description`, `--supported-values`, and `--project` as required. Requiredness must reflect the option's required setting rather than the minimum arity of a supplied option value, otherwise help gives users invalid invocation constraints.

### P1: Group help leaks implementation details from unpresented nested commands

The fallback to `action.Description` in [CommandHelpRenderer.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/CommandHelpRenderer.cs:75) exposes raw implementation descriptions for nested commands that have no presentation entry. `mo agent model --help`, for example, prints `Uses GET /api/projects/{projectId}/opencode/models.` from [MohistCliCommands.AgentModel.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.AgentModel.cs:18). The help specification explicitly forbids API routes and implementation detail. Attach user-facing presentation metadata throughout nested command trees, or prevent raw descriptions from being rendered in custom help.

### P1: The command surface still accepts removed resource verbs and aliases

The migration leaves several noncanonical public paths executable: `mo label update` is registered at [MohistCliCommands.Label.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Label.cs:124), while `mo issue template get` and the `mo issue template ls` alias remain at [MohistCliCommands.Issue.Template.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Issue.Template.cs:18) and [MohistCliCommands.Issue.Template.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Issue.Template.cs:48). The command-language spec requires resource mutation to use `edit`, resource reads to use `view`, and removes alternate aliases. Rename these paths, update callers/examples, and add parser rejection tests for the removed forms.

### P1: Some local usage errors bypass the shared scoped error renderer

`session list` validates missing or conflicting selectors inside its handler and returns exit code 1 with only a diagnostic at [MohistCliCommands.Session.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Session.cs:104). Likewise, `--project-id` is intercepted before parse-error rendering at [MohistCliCommands.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.cs:237), so it returns no nearest-command usage. Both cases are invalid local input and must exit 2 with a specific stderr diagnostic plus scoped usage, without resolution or requests. Route them through the shared usage-error path and cover both scenarios.

### P1: Required stdin form is unsupported and deprecated stdin flags remain public

The command-language spec requires `--<name>-file -` (or `--file -`) as the sole stdin form for long text. `BodyInputResolver` instead attempts to read `-` as a file at [BodyInputResolver.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/BodyInputResolver.cs:81), so `printf body | mo issue create title --body-file -` fails. Meanwhile `--body-stdin` remains public at [MohistCliCommands.Issue.CrudWrites.cs](/home/szf/.mohist/projects/workspaces/wr_dda5615218a9496aa68f2b30db2217a7/packages/cli/Mohist.Cli/MohistCliCommands.Issue.CrudWrites.cs:15), with equivalent `--prompt-stdin` and `--text-stdin` flags on Agent and Session commands. Interpret a `-` file value as standard input centrally, remove the alternate stdin options, and add parser/behavior coverage across each affected long-text input.

## Verification

`dotnet test Mohist.sln --filter FullyQualifiedName~Mohist.Cli.Tests` passed: 1,377 CLI tests. `git diff --check master...HEAD` also passed.

<promise>FAIL</promise>
