# Core Actions

Core Actions are deterministic local primitives. They run processes, scripts,
and file checks directly on the Runner without an execution backend or a
model, so a Workflow can express setup and verification steps whose outputs
and error codes recovery rules match exactly.

## `core/process`

Runs a process and captures its standard output and exit code.

```yaml
- id: check-version
  uses: core/process
  with:
    command: node
    args: [--version]
```

Inputs:

- `command` (required, text): command to invoke.
- `args` (optional, default `[]`): arguments to pass to the command.

Outputs:

- `stdout`: command standard output with leading and trailing whitespace
  removed.
- `exitCode`: process exit code.

Business error codes:

- `process-failed`: the process exited with a nonzero status.

## `core/script`

Runs an inline script through the current platform's shell wrapper.

```yaml
- id: verify-diff
  uses: core/script
  with:
    run: git diff --check
```

Inputs:

- `run` (required, text): script content to run.
- `shell` (optional, text): shell executable.
- `timeout` (optional, numeric): script execution deadline in milliseconds.

Outputs:

- `kind`: output type identifier.
- `run`: original script content.
- `shell`: shell executable that was used.
- `exitCode`: shell exit code.
- `stdout`: truncated standard output.
- `stderr`: truncated standard error.

Business error codes:

- `script-failed`: the script exited with a nonzero status.

A script-failure diagnostic includes the shell exit code and bounded tails of
nonempty standard output and standard error. Recovery therefore receives an
actionable failure reason even when a tool writes warnings to standard error
and the actual failure to standard output.

## `core/artifact-exists`

Checks whether a file or directory exists at a relative workspace path.

```yaml
- id: check-proposal
  uses: core/artifact-exists
  with:
    path: openspec/changes/issue-448/proposal.md
```

Inputs:

- `path` (required, text): path to check.

Outputs:

- `kind`: output type identifier.
- `path`: resolved path.
- `exists`: whether the path exists.

Business error codes:

- `artifact-missing`: the required file or directory does not exist.

## `core/marker`

Checks whether a workspace file contains specified marker text.

```yaml
- id: verify-completion
  uses: core/marker
  with:
    path: openspec/changes/issue-448/progress.txt
    expect: "## Codebase Patterns"
```

Inputs:

- `path` (required, text): path to read.
- `expect` (optional, text): marker text to match.

Outputs:

- `kind`: output type identifier.
- `path`: resolved path.
- `marker`: marker text matched by this check.
- `found`: whether the marker was found.

Business error codes:

- `artifact-missing`: the marker file does not exist.
- `marker-missing`: the marker text was not found in the file.
