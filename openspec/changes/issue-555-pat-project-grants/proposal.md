## Why

The planned External Agent API needs a credential-owned Project boundary before
any direct route can authenticate or authorize a caller. Existing PATs cannot
express that boundary, and the CLI cannot issue a token for a named Project.

## Scope

This change adds only PAT Project grants and grant-aware token issuance. It
does not register `/api/v1` routes, resolve an external caller, expose an
execution read, or change the authority of existing control-plane routes.

## Change

- A PAT can persist either an explicit non-empty set of Project IDs or an
  explicit `operator_all` grant.
- `mo auth token create` accepts repeatable `--project` or `--all-projects`.
- Grant references are resolved before issuance; invalid combinations or
  bindings leave no Credential or grant rows.

## Safety

The grant is inert until the later direct API authorization slice consumes it.
PATs without a grant retain their existing control-plane behavior, while no
new external execution route becomes reachable in this change.
