---
name: Refactor
description: "Internal quality — refactoring, test coverage, optimization. Signal: external behavior is unchanged; the value is internal (maintainability/reliability/performance ceiling)."
---

## Motivation

<!-- The real cost of this debt: what it blocks, where it recently hurt, why now. 'I don't like the code' is not a valid motivation. -->

<What this debt costs: what it blocks, where it recently hurt, why now.>

## Change Scope

<!-- Refactor scope + technique (split/extract/inline/introduce indirection), at the product level, bounded. Leave concrete steps to plan. Out-of-scope cleanup goes to Non-Goals. -->

<What gets restructured and how, at the product level.>

## Behavior Contract

<!-- Required, the heart of a refactor: list each 'external behavior that must NOT change' + the safety net (existing/new characterization tests). A contract without a safety net is empty — either add tests or shrink scope. -->

<Behaviors that must NOT change, and the safety net (existing/new tests) proving it.>

## Done When

<!-- At least one measurable structural metric (a number) + the safety net green. 'Cleaner code' is forbidden. The metric must map 1:1 to a pain stated in Motivation. -->

- [ ] <Measurable structural improvement>
- [ ] <Safety net green: behavior unchanged>

## Non-Goals

<!-- Anti-gold-plating: out-of-scope polish, abstractions that shouldn't be introduced, healthy code that shouldn't be rewritten. -->

- <Explicit out-of-scope polish or abstraction>
