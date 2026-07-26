## Findings

### P1: The plan advertises `mo help <topic>` but never implements or tests it

[`cli-command-language/spec.md`](specs/cli-command-language/spec.md#canonical-command-areas) requires `help` as a canonical root task, and [`cli-help/spec.md`](specs/cli-help/spec.md#root-help-as-a-capability-index) requires root help to expose `mo help <topic>`. The current root registrations have no `help` command, and neither the design nor T-001/T-002 specifies the supported topics, command handler, rendering source, or a resolving command test. T-002 only covers `--help` output and parser failures, so an implementation can display a dead `mo help <topic>` reference while still satisfying every listed acceptance criterion.

Add the topic command as a planned capability with its owned content (at least `output`, `environment`, and `exit-codes`), local execution behavior, unknown-topic usage error, and focused parser/output tests. The root-help criterion must then assert that each advertised topic is executable.

### P1: Every task uses a nonexistent CLI workspace test command

[`tasks.json`](tasks.json#L14), [`tasks.json`](tasks.json#L33), and [`tasks.json`](tasks.json#L52) require `npm test -w packages/cli`. The repository declares npm workspaces only for `packages/web` and `packages/runner`, and `packages/cli/package.json` does not exist; this command cannot verify any task. The repository testing contract instead uses root `npm test` for the .NET solution.

Replace these acceptance criteria with an executable focused CLI test command and the required root-level verification command. This gives autonomous execution a command that can actually satisfy the acceptance criteria.

<promise>FAIL</promise>
