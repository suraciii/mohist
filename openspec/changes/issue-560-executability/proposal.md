## Why

Direct Agent creation already persists a definition and the Server already
checks part of that definition before it creates a Job. The product still
labels the result as `Ready`, `Needs setup`, or `Unknown`, which hides the
important distinction between an incomplete definition and a definition that
the runtime has rejected. It also leaves different launch entry points able to
interpret the same facts differently.

## What Changes

- Replace the public Agent readiness projection with the four Server-derived
  executability states: `not-configured`, `not-executable`, `unknown`, and
  `executable`.
- Return per-gap next actions and a concrete fix entry point with the Agent
  projection. Do not persist a verdict; derive it on each definition read.
- Use that projection at every Agent launch admission boundary. Both blocked
  states reject before a Job or Session is created; unknown and executable
  continue through the established launch path.
- Render the same Server result in the Web Agent list/detail/composer and
  `mo agent view`. Availability remains a separately named, transient signal.

## Non-Goals

- Agent purpose/permission authoring, launch-scope preview, and runtime tool
  permission enforcement are separate #560 slices.
- This change does not alter Runner capacity, connection setup, or the
  Availability endpoint.
