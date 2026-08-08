---
name: Feature request
about: "Product feature work — new features, or iteration/rework of existing features. Signal: external behavior changes in a user-perceivable way."
labels: [enhancement]
---

<!--
Choose the template by external behavior:
  changed in a user-perceivable way → Feature request
  unchanged, but fixing something wrong → Bug report
  unchanged, already correct, internal only → Refactor
This is a requirements intake, not a solution sketch: say what the user
needs, not how to build it. The smallest thing that demonstrates the need
beats a grand vision.
-->

## User Voice

<!--
Write the user's own need in the first person: what they're trying to do,
where they get stuck, what success looks like. Record the user's words, not a
solution. No product jargon, no implementation. Minimum one sentence — never
skip this section.
-->

<What are you trying to do? Where do you get stuck?>

## Product Shape

<!--
The product decision: what the user can see/do after the change, the in/out
scope boundary, and the trade-offs. Do NOT cite source paths, file names, or
symbol names — describe the product form. Make the trade-offs explicit.
-->

<What changes in the product? What is the boundary — in scope and out of scope?>

## Domain Model

<!--
Optional: keep only when the work touches a non-trivial domain (invariants /
lifecycle / cross-aggregate constraints). Pure UI / copy / technical fixes:
delete this whole section. State domain concepts in the domain's own
vocabulary — not files, symbols, or an implementation recipe.
-->

<Optional. What are the key domain concepts? What invariants and constraints shape this?>

## Acceptance Criteria

<!--
Observable, verifiable conditions from the user perspective, one [ ] per line.
Forbidden: implementation-level checks ('unit tests pass', 'migration runs').
-->

- [ ] <Observable, verifiable condition from the user perspective>
- [ ] <Observable, verifiable condition>

## Non-Goals

<!--
Explicit out-of-scope items: things a reader might expect but are
deliberately excluded. Be brave — actually cut things, this sharpens the
boundary.
-->

- <Explicit out-of-scope item>

---

> Add the `mohist` label to route this issue into the Mohist pipeline; `p0`–`p4` labels map to priority.
