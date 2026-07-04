---
name: Refactor
description: "Internal quality — refactoring, test coverage, optimization. Signal: external behavior is unchanged; the value is internal (maintainability/reliability/performance ceiling)."
---

## Motivation

<!--
The real cost of this debt: what it blocks, where it recently hurt (cite the concrete pain — e.g. 'adding a stage touches 3 unrelated sites'), and why now.
Forbidden: 'I don't like the code', 'not elegant enough' — subjective motivations with no cost evidence.
Do not write the solution here — that is Change Scope.
-->

<What this debt costs: what it blocks, where it recently hurt, why now.>

## Change Scope

<!--
What gets restructured and how, at the product level: split / extract / inline / introduce an indirection.
Keep it bounded — state in one sentence what this refactor does.
Leave concrete files / functions / step-by-step to the Plan stage. Out-of-scope cleanup goes to Non-Goals.
-->

<What gets restructured and how, at the product level.>

## Behavior Contract

<!--
Required — the heart of a refactor. List each 'external behavior that must NOT change' (user / API / other modules), one per line.
Then name the safety net: which existing tests cover it, which characterization test / golden master needs adding.
A contract without a safety net is empty: either add the test, or shrink the scope until it is covered.
Do not pass behavior changes off as refactor — that is a Feature or a Bug.
-->

<Behaviors that must NOT change, and the safety net (existing/new tests) proving it.>

## Done When

<!--
At least one measurable structural metric (a number: file < N lines, N+1 eliminated, coverage %, cyclomatic complexity < X) + the safety net green.
Forbidden: 'cleaner code', 'more maintainable' — unmeasurable.
The metric must map 1:1 to a pain stated in Motivation.
-->

- [ ] <Measurable structural improvement>
- [ ] <Safety net green: behavior unchanged>

## Non-Goals

<!--
Anti-gold-plating — more important here than for Feature/Bug.
Out-of-scope polish, abstractions that shouldn't be introduced, healthy code that shouldn't be rewritten.
-->

- <Explicit out-of-scope polish or abstraction>
