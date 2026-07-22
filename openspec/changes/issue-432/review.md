# Review Findings

## P1: YAML type errors are accepted for scalar fields

`packages/server/src/Mohist.Workflow.Definition/WorkflowDefinitionParser.cs:662-705`
checks only whether a value is a `YamlScalarNode`; it does not verify that the
scalar has string semantics. Consequently numeric and boolean values are
accepted for `stage`, task/check `id`, `uses`, `title`, recovery `when`, and
artifact/setVars string values. For example, the CLI currently reports this
Definition as valid even though all three fields have the wrong types:

```yaml
stages:
  - stage: 123
    tasks:
      - id: 1
        uses: true
    checks: []
```

This violates the acceptance criterion that type mismatches must be reported
instead of being accepted/coerced, and lets an invalid semantic model reach the
save path. The scalar readers need to distinguish quoted/string scalars from
numeric and boolean YAML scalars, with errors at each offending YAML path.

## P1: Additional YAML documents are silently ignored

`packages/server/src/Mohist.Workflow.Definition/WorkflowDefinitionParser.cs:34-41`
checks only that at least one document exists, then parses `Documents[0]` and
ignores every subsequent document. Therefore a valid Definition followed by a
second document containing arbitrary invalid content is accepted by
`mo workflow validate` and by Profile parsing. For example:

```yaml
stages:
  - stage: build
    tasks: []
    checks: []
---
unknown: true
```

The Definition language requires one structured YAML document; silently
discarding the rest violates the illegal-structure/unknown-field acceptance
criteria and can make the validator disagree with the input the author saved.
Reject multiple documents with a Definition error (or validate every document
and report the extra-document path) before accepting the first one.

## P2: Null stored Definitions escape the load-path validation error contract

`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfilePersistence.cs:12-28`
deserializes a persisted JSON Profile and passes `profile.Definition` directly
to `WorkflowDefinitionValidator.Validate`. A stored payload with
`"definition": null` produces a null record despite the non-nullable annotation;
`Validate` then calls `ArgumentNullException.ThrowIfNull` instead of returning a
`ValidationError`/`WorkflowDefinitionValidationException`. The load path thus
turns a malformed persisted Definition into an unhandled exception (typically a
500), contrary to the design's requirement that deserialized stored Definitions
surface a clear Definition validation failure rather than being silently or
implicitly mishandled. Handle the null Definition before calling `Validate` and
return the same structured Definition error used for other invalid stored
Profiles.

<promise>FAIL</promise>
