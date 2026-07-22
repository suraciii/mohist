# Review Findings

## P1: YAML numeric forms are still accepted as strings

`packages/server/src/Mohist.Workflow.Definition/WorkflowDefinitionParser.cs:549-588`
classifies string fields through `IsStringScalar`, but recognizes numeric
scalars only when `long.TryParse`/`double.TryParse` accept the raw spelling.
YAML numeric forms such as hexadecimal `0x10`, and other valid YAML integer
spellings such as `1_000`, do not match those .NET parsers and therefore fall
through as JSON strings.

For example, this input is currently reported as valid by
`mo workflow validate --file -`, even though `stage` is not a YAML string:

```yaml
stages:
  - stage: 0x10
    tasks: []
    checks: []
```

The same failure is reachable from Profile save because it uses the shared
parser. This violates the type-error acceptance criterion: scalar
classification must recognize the complete YAML numeric forms and return a
`ValidationError` at `stages[0].stage` (and likewise for nested `with` values),
instead of treating a numeric scalar as a string.

<promise>FAIL</promise>
