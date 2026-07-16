# CLI Design

`mo` is the CLI. Scripts, CI, agents, SSH depend on its shape. This spec defines the contract. Concrete commands, verbs, and flags live in `docs/cli-reference.md`.

## Command shape

`mo <noun> <verb>`. Noun first. Like `mo workflow approve`, not `approve workflow`.

- Root level: only nouns (resources). Never bare verbs.
- One exception: cross-resource read-only diagnostics — must justify explicitly.
- Command name must match behavior. No mismatch.

## Resource naming

- One noun per concept. Never two paths for the same thing.
- Top-level `mo <noun>` = independent aggregate root.
- Sub-resources nest under their parent noun.
- Scope uses flag (`--project`), not path segment. Like kubectl `--namespace`.
- One word = one domain meaning. No overloaded terms.

`mo workflow` = WorkflowRun (aggregate root).
`mo project workflow profile` = WorkflowProfile (project-scoped collection).

## Verb consistency

- Same action class = same verb across all resources.
- Domain lifecycle actions (approve, retry, pause) use domain language, not CRUD.
- Verb name reflects idempotency and destructiveness.

## Single entry

One capability = one command path. Never two.

## Flag over command

Variants use flags, not sibling commands. `mo workflow rerun --from-stage <stage>`, not `rerun-from-stage`.

## Read output: three types

| Type | Means | Example |
|---|---|---|
| output format | same resource, different render | `-o yaml` |
| sub-resource | independent path, addressable | `mo workflow variables <runId>` |
| collection | one-to-many child items | `mo workflow events <runId>` |

Output formats are never commands. Sub-resources are never output formats. Collections are never either.

## Global flags

- All applicable commands accept global flags consistently.
- Project reference accepts all canonical forms.
- Shared flags: factory-built, declared once. New commands reuse the factory.
- Same meaning = same flag name. Never synonyms.

## Aliases

Short aliases for frequent commands. Same behavior as canonical name. Optional convenience layer.
