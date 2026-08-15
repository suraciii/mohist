## Why

An Agent definition can describe how work runs, but it cannot separately state
what the Agent is for or the declared scope of operations it may perform.
Those facts are therefore absent from definition reads and cannot be authored
consistently through the CLI and Web profile editor.

## What Changes

- Add an optional task `purpose` and a declared `permissions` list to the
  persisted Agent definition.
- Validate declared permissions against one Server-owned closed vocabulary at
  the Agent create and update boundary.
- Return both fields from every Agent definition projection and make them
  authorable and readable through the existing CLI and Web profile surfaces.

## Non-Goals

- Enforcing declared permissions in an Agent runtime or Runner tool policy.
- Pre-launch scope confirmation or recording permissions as launch facts.
- Migrating an existing description into purpose, or changing existing launch
  snapshots.
