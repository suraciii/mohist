# Design

## Scope

`core/script` already resolves `resourceProfile: full-verify` at the Runner
action boundary. Change the profile constant from 4096 MiB to 16384 MiB and
retain the existing wall-clock and RSS watchdog values from the host. Linux
continues to use `prlimit --as/--data`; hosts without `prlimit` keep the
existing watchdog fallback. The Runner service aggregate cgroup/systemd limit
remains the outer protection for concurrent work.

The workflow YAML remains unchanged because both built-in profiles already opt
into the named profile. No live Run is retried or mutated by this change.

## Evidence boundary

On current-master, the representative Server UnitTests suite failed with a
4 GiB bound and again at 8 GiB during MSBuild copy, while a 16 GiB bound passed
2765/2765 with zero failed, skipped, or not-run tests. The profile therefore
needs a larger finite address-space budget; this is not evidence for removing
containment or using an unbounded command.

## Tests

- Runner resource-profile unit coverage asserts the resolved bound is 16384 MiB
  and preserves the existing watchdog/wall-clock values.
- Script action coverage asserts the named profile reaches the command runner
  with the same finite bound.
- Existing resource-containment tests remain unchanged and continue to cover
  the default per-work bound and fallback watchdog behavior.
