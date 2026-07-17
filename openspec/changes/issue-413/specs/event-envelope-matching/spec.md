### Requirement: Boolean expression grammar with defined precedence

A match expression SHALL be a single boolean expression built from logical OR (`||`), logical AND (`&&`), logical NOT (`!`), equality (`==`), inequality (`!=`), membership (`in`), the string functions `startsWith`, `endsWith`, `contains`, and `matches`, and the presence test `has()`. Parentheses SHALL group sub-expressions. Precedence, from lowest to highest, SHALL be `||`, then `&&`, then unary `!`, then comparisons, calls, and presence tests. The grammar SHALL be a subset of [CEL](https://cel.dev/), so every accepted expression is also a valid CEL expression and remains valid if the implementation is later replaced by a full CEL evaluator.

#### Scenario: AND binds tighter than OR

- **WHEN** the expression `event.type == "x" || event.type == "y" && event.issue == "1"` is evaluated against an event with `type = "x"` and no `issue`
- **THEN** it SHALL match
- **AND** the result SHALL equal evaluating `event.type == "x" || (event.type == "y" && event.issue == "1")`

#### Scenario: Parentheses override precedence

- **WHEN** the expression `(event.type == "x" || event.type == "y") && event.issue == "1"` is evaluated against an event with `type = "x"` and no `issue`
- **THEN** it SHALL NOT match

#### Scenario: NOT negates a presence test

- **WHEN** the expression `!has(event.epic)` is evaluated against an event whose envelope carries no `epic`
- **THEN** it SHALL match

### Requirement: All values are strings

Every literal in an expression SHALL be a double-quoted string literal. The grammar SHALL NOT provide numeric, boolean, or null literals, arithmetic, index/collection access, or nested field access beyond `event.<ident>`. The operands of `==` and `!=` SHALL each be an attribute reference or a string literal, and every element of an `in` list SHALL be a string literal.

#### Scenario: Numeric literal is rejected

- **WHEN** an expression containing a numeric literal such as `event.issue == 42` is compiled
- **THEN** the compile SHALL fail

#### Scenario: Boolean literal is rejected

- **WHEN** an expression containing `true` or `false` as a literal is compiled
- **THEN** the compile SHALL fail

### Requirement: Matchable attribute namespace

`event.<attr>` SHALL resolve CloudEvent envelope attributes as strings. `event.type`, `event.source`, and `event.subject` SHALL resolve to the corresponding core envelope fields. Any other `event.<ident>` SHALL resolve to the context extension attribute of that name (for example `event.issue`, `event.epic`, `event.workflowrunid`, `event.stage`, `event.projectid`). Core fields and extensions SHALL have equal standing as string operands.

#### Scenario: Match on event type

- **WHEN** the expression `event.type == "com.mohist.issue.completed"` is evaluated against an event of that type
- **THEN** it SHALL match

#### Scenario: Match on a context extension

- **WHEN** the expression `event.issue == "42"` is evaluated against an event whose envelope carries `issue = "42"`
- **THEN** it SHALL match

### Requirement: Missing attributes compare as the empty string

An attribute that is absent from the envelope SHALL resolve to the empty string `""` when used as a comparison operand, an `in` operand, or a function receiver. A comparison or function over a missing attribute SHALL behave identically to the same operation over an attribute that is present with the empty string.

#### Scenario: Missing attribute equals the empty string

- **WHEN** the expression `event.epic == ""` is evaluated against an event whose envelope carries no `epic`
- **THEN** it SHALL match

#### Scenario: startsWith over a missing attribute

- **WHEN** the expression `event.epic.startsWith("7")` is evaluated against an event whose envelope carries no `epic`
- **THEN** it SHALL NOT match

#### Scenario: Missing attribute is absent from an in list

- **WHEN** the expression `event.stage in ["plan", "build"]` is evaluated against an event whose envelope carries no `stage`
- **THEN** it SHALL NOT match

### Requirement: has() distinguishes absent from present-but-empty

`has(event.<attr>)` SHALL return whether the attribute is present on the envelope, so a present-but-empty value is distinguishable from an absent one. For an extension attribute, presence SHALL mean the extension key exists on the envelope. `has()` over a present-but-empty attribute SHALL return true, while a direct equality to `""` SHALL also return true, so the two tests can be combined to separate the two cases.

#### Scenario: has() is false for an absent attribute

- **WHEN** the expression `has(event.epic)` is evaluated against an event whose envelope carries no `epic`
- **THEN** it SHALL NOT match

#### Scenario: has() is true for a present attribute

- **WHEN** the expression `has(event.epic)` is evaluated against an event whose envelope carries `epic = "7"`
- **THEN** it SHALL match

#### Scenario: has() distinguishes empty from absent

- **WHEN** the expression `has(event.subject) && event.subject == ""` is evaluated against an event whose envelope has a present but empty `subject`
- **THEN** it SHALL match
- **WHEN** the same expression is evaluated against an event whose envelope has no `subject`
- **THEN** it SHALL NOT match

### Requirement: Equality, inequality, and membership

`==` and `!=` SHALL compare two string operands by ordinal, case-sensitive equality. `in` SHALL test whether the attribute's resolved string value equals any string in a bracketed list of string literals. `in` SHALL accept a list of one or more string literals; an empty list (`[]`) SHALL match nothing.

#### Scenario: Equality is ordinal and case-sensitive

- **WHEN** the expression `event.type == "com.mohist.issue.completed"` is evaluated against an event with `type = "COM.MOHIST.ISSUE.COMPLETED"`
- **THEN** it SHALL NOT match

#### Scenario: in matches a member

- **WHEN** the expression `event.issue in ["42", "43"]` is evaluated against an event with `issue = "43"`
- **THEN** it SHALL match

#### Scenario: in rejects a non-member

- **WHEN** the expression `event.issue in ["42", "43"]` is evaluated against an event with `issue = "44"`
- **THEN** it SHALL NOT match

### Requirement: String functions

`startsWith`, `endsWith`, `contains`, and `matches` SHALL be invoked as `event.<attr>.<func>("<arg>")` and SHALL compare the attribute's resolved string value against the argument string. `startsWith`, `endsWith`, and `contains` SHALL perform ordinal substring tests. `matches` SHALL test whether the attribute's value matches the argument interpreted as a regular expression.

#### Scenario: startsWith

- **WHEN** the expression `event.type.startsWith("com.mohist.workflow.")` is evaluated against an event with `type = "com.mohist.workflow.run.failed"`
- **THEN** it SHALL match

#### Scenario: endsWith

- **WHEN** the expression `event.source.endsWith("/issues/42")` is evaluated against an event with `source = "/mohist/projects/p/issues/42"`
- **THEN** it SHALL match

#### Scenario: contains

- **WHEN** the expression `event.type.contains("approval")` is evaluated against an event with `type = "com.mohist.workflow.stage.approval-requested"`
- **THEN** it SHALL match

### Requirement: matches rejects invalid patterns at compile time and is bounded by a timeout at evaluation

The argument to `matches` SHALL be compiled as a regular expression when the expression itself is compiled. An argument that is not a valid regular expression SHALL be rejected at compile time. At evaluation, `matches` SHALL be bounded by a timeout. Exceeding the timeout, or any other regular-expression runtime failure, SHALL be treated as a non-match rather than propagated as an error.

#### Scenario: Invalid regex is rejected at compile time

- **WHEN** an expression such as `event.type.matches("[")` is compiled
- **THEN** the compile SHALL fail with a diagnostic identifying the offending `matches` argument

#### Scenario: Regex timeout is a non-match

- **WHEN** a `matches` call whose compiled pattern requires excessive backtracking is evaluated against a value that drives it past the timeout
- **THEN** it SHALL be treated as a non-match
- **AND** SHALL NOT raise an error that propagates to the caller

### Requirement: Payload access is rejected

The matcher SHALL evaluate only the event envelope. `event.data` and any access into event payload content SHALL NOT be addressable; referencing `event.data` SHALL be rejected at compile time. A business dimension that is needed for matching SHALL be promoted to an envelope context attribute, never read from payload.

#### Scenario: event.data is rejected

- **WHEN** an expression such as `event.data.status == "failed"` is compiled
- **THEN** the compile SHALL fail

### Requirement: Deterministic and terminating evaluation

Evaluation SHALL be deterministic: the same compiled expression evaluated against the same event envelope SHALL always produce the same result. Evaluation SHALL be guaranteed to terminate: the grammar SHALL NOT permit recursion, loops, or user-defined functions, and the only potentially unbounded operation (`matches`) SHALL be bounded by its timeout.

#### Scenario: Repeated evaluation is stable

- **WHEN** a compiled expression is evaluated against the same event envelope one thousand times
- **THEN** every evaluation SHALL produce the same result

### Requirement: Compile-time validation with error location

An expression SHALL be compiled before it is used. A syntax error SHALL be rejected at compile time with a diagnostic that identifies the location of the error in the source text. A successfully compiled expression SHALL be reusable for evaluating many events without re-parsing.

#### Scenario: Syntax error reports a location

- **WHEN** an expression with unbalanced parentheses such as `(event.type == "x"` is compiled
- **THEN** the compile SHALL fail
- **AND** the diagnostic SHALL identify a location in the expression

#### Scenario: A compiled expression evaluates many events

- **WHEN** a compiled expression is applied to a sequence of distinct event envelopes
- **THEN** each envelope SHALL be evaluated against the same compiled form without re-parsing

### Requirement: Runtime evaluation failure is a non-match

An exception raised while evaluating a compiled expression against an event (other than a regex timeout, which is already specified as a non-match) SHALL be treated as a non-match and recorded in a structured log and counter, rather than propagated to the caller or terminating an in-progress stream of events.

#### Scenario: Evaluation exception does not propagate

- **WHEN** evaluating a compiled expression against an event raises an exception at runtime
- **THEN** the event SHALL be treated as a non-match
- **AND** the failure SHALL be recorded in a structured log and counter
- **AND** the exception SHALL NOT propagate to the caller
