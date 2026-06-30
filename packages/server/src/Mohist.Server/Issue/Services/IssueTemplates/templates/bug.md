---
name: Bug
description: "Fix — functional bugs (wrong behavior) or non-functional bugs (performance/reliability/resource). Signal: behavior deviates from the correct state, i.e. an invariant is violated."
---

## Symptom & Evidence

<!-- Pick the mode first: functional = repro steps from a known state + Expected vs Actual; non-functional = current measured value + target + how it was measured. 'A bit slow' is forbidden. -->

<Functional: repro steps from a known state + Expected vs Actual. Non-functional: current measured value + target + how it was measured.>

## Domain Context

<!-- Required: state the violated invariant ('system should X, actually Y'). For pure typo/copy bugs, shrink to one line or delete the section. No fix proposal. -->

<The invariant that should hold, and how the current state violates it.>

## Fix Shape

<!-- Correction direction + blast boundary (what you touch / don't touch). Stay minimal: change only what restores the invariant. Leave concrete files/functions to plan. -->

<The correction direction and what is in/out of scope.>

## Acceptance Criteria

<!-- Functional = behavior is correct along the repro path; non-functional = the metric meets target (give the number). One [ ] per line, no implementation-level checks. -->

- [ ] <Functional: observable correct behavior; Non-functional: metric meets target>
- [ ] <...>

## Non-Goals

<!-- Adjacent bugs not fixed here, and boundaries deliberately not expanded. -->

- <Explicit out-of-scope item>
