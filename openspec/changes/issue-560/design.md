# Design: Task Profile Authoring Slice

## Scope

This first #560 product slice makes the task profile visible and durable for
an Agent created or edited through the CLI. It adds the optional `purpose` and
declared `permissions` fields to the existing Agent definition, validates
permissions at the Server write boundary, and projects both fields through the
existing create, edit, and view routes.

`mo agent create` and `mo agent edit` are the first consumer. Their requests
use the same POST/PATCH routes as every other Agent definition client, so a
clear in the CLI is a durable clear rather than CLI-local state. `mo agent
view` renders the saved profile so the user can inspect the declaration before
using the existing launch command.

## Contract

The Server owns one closed permission vocabulary:

`repo:read`, `repo:write`, `issue:read`, `issue:write`, `epic:read`,
`epic:write`, and `artifact:publish`.

An omitted declaration and a cleared declaration both project as an empty
list. An unknown, empty term, or non-string term rejects the complete create
or patch before the Agent grain is called, so no partial profile is persisted.
`purpose: null` and `permissions: []` are presence-tracked PATCH clears.
The CLI rejects an explicitly empty `--permissions` value; only
`--clear-permissions` can clear an existing declaration.

This is a declaration and display slice. Existing launch admission and runtime
execution stay unchanged: permissions are not yet a Runner tool policy, and
launch-scope preview, executability, model guidance, and Web editing remain
separate follow-up slices. The existing Agent launch path therefore continues
to start the Agent exactly as before while the profile is safely durable and
observable.

## Alternatives

- **Server fields without a consumer:** rejected because it would leave the
  task profile write-only.
- **Full launch-scope/executability migration:** rejected for this slice
  because it changes admission, launch snapshots, CLI confirmation, and Web
  rendering together. Those are a later vertical slice with a different
  terminal behavior contract.
