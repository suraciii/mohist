---
name: Bug report
about: "Fix — functional bugs (wrong behavior) or non-functional bugs (performance/reliability/resource). Signal: behavior deviates from the correct state, i.e. an invariant is violated."
labels: [bug]
---

<!--
Choose the template by external behavior:
  changed in a user-perceivable way → Feature request
  unchanged, but fixing something wrong → Bug report
  unchanged, already correct, internal only → Refactor
Fill in each section with the minimum the fixer needs. Concrete > thorough:
an example beats a paragraph. Never write "it's slow" — give a number.
-->

## Symptom & Evidence

<!--
Pick the mode first:
  functional     = repro steps from a known state + Expected vs Actual (state
                   what triggers it and the data/objects involved).
  non-functional = current measured value + target + how it was measured (a
                   number and a method, always).
Forbidden: 'a bit slow', 'sometimes crashes', 'feels off' — anything
unreproducible or unquantifiable. Do not jump to the root cause or the fix
here — that belongs in Domain Context / Fix Shape.
-->

<Functional: repro steps from a known state + Expected vs Actual.
 Non-functional: current measured value + target + how it was measured.>

## Domain Context

<!--
Required: state the violated invariant — 'the system should X, actually Y'.
Name the domain concepts and their intended relationship; cite the code path
only if it carries the concept. For a pure typo / copy bug: shrink to one
line, or delete this whole section. Do NOT propose the fix here.
-->

<The invariant that should hold, and how the current state violates it.>

## Fix Shape

<!--
The correction direction + the blast boundary (what you touch / don't touch).
Stay minimal: change only what restores the invariant, not what is 'more
elegant'. Leave concrete files / functions / schemas to the Plan stage.
Adjacent bugs you noticed go to a separate issue, not here.
-->

<The correction direction and what is in/out of scope.>

## Acceptance Criteria

<!--
Functional = the behavior is correct along the repro path; Non-functional =
the metric meets the target (give the number). One [ ] per line. Forbidden:
implementation-level checks ('unit tests pass').
-->

- [ ] <Functional: observable correct behavior; Non-functional: metric meets target>
- [ ] <...>

## Non-Goals

<!--
Adjacent bugs not fixed here, and boundaries deliberately not expanded.
-->

- <Explicit out-of-scope item>

---

> Add the `mohist` label to route this issue into the Mohist pipeline; `p0`–`p4` labels map to priority.
