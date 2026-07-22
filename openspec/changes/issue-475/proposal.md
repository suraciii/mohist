## Why

Agents currently have to accommodate inconsistent project flags, JSON modes and shapes, diagnostics, exit codes, and interactive behavior across `mo` command families. As the CLI becomes the agent-facing control surface, these ambiguities can cause excess context, stalled automation, false success, or duplicate state changes; a shared contract is needed before later domain-command work expands the surface further.

## What Changes

- Introduce one shared execution contract for all existing `mo` commands, covering project resolution, machine-readable resource output, diagnostics, exit outcomes, and non-interactive execution.
- **BREAKING** Replace the dual project flags with `--project <name-or-id>` as the only Project-scoped command input; resolve it from the explicit flag, current-directory context, or locally selected Project, and report ambiguity with an actionable diagnostic.
- **BREAKING** Replace command-specific `--output` and boolean `--json` modes on resource reads with `--json <fields>`: an explicit field selection returns only those fields, while bare `--json` lists the command's available fields locally without contacting Mohist services.
- Standardize successful machine output as a single object for one resource, an array for a collection, and NDJSON for continuous event or log streams, with no general response envelope.
- Keep results exclusively on stdout and diagnostics, progress, confirmation, errors, and recovery hints exclusively on stderr; make JSON and NDJSON independent of human-oriented rendering.
- Standardize exit outcomes for success, operation failure, command usage failure, and user cancellation; retain stable domain error codes and emit one executable hint only when the recovery action is unambiguous.
- Make non-interactive invocations fail immediately for missing required input rather than prompt, and classify mutating-request transport failures so an unknown submission outcome is never automatically retried.

## Capabilities
- `cli-project-reference`: Project-scoped command selection through the single `--project <name-or-id>` reference, its ordered local resolution sources, and actionable unresolved or ambiguous-reference diagnostics.
- `cli-resource-output`: Field discovery and selected-field JSON output for resource reads, including object, array, and NDJSON result shapes and stdout-only result delivery.
- `cli-execution-contract`: Shared diagnostics, stable error and exit semantics, non-interactive prompting rules, and safe handling of transport failures for state-changing requests.

## Impact

- **CLI command surface** (`packages/cli/Mohist.Cli`): all Project-scoped commands currently register both `--project` and `--project-id`; resource commands currently use a mix of `--output` and command-specific boolean `--json` flags.
- **CLI shared infrastructure**: `MohistCliCommands`, `MohistCliApi`, table renderers, NDJSON streaming, prompt paths, and HTTP request handling must converge on the shared contract.
- **CLI tests** (`packages/cli/tests/Mohist.Cli.Tests`): broad command-shape, output, error, project-resolution, stream, and non-interactive behavior coverage changes with the public contract.
- **Automation consumers**: scripts and agents must migrate from `--project-id`, `--output`, and existing command-specific JSON shapes to the new Project and field-selection interfaces.
- No Server API, Runner protocol, persisted model, database schema, or external dependency change is required.
