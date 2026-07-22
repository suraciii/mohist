# Review Findings

## P1: Type errors produce misleading cascading "required" errors

`packages/server/src/Mohist.Workflow.Definition/WorkflowDefinitionParser.cs:703-750`
returns `null` after reporting a wrong scalar type. The semantic walk then
validates that same `null` model value in
`packages/server/src/Mohist.Workflow.Definition/WorkflowDefinitionRules.cs:135-147`
and emits a second required/empty-identifier error. The new `(path,message)`
deduplication intentionally preserves both messages, so this is observable in
the CLI.

For example, validating:

```yaml
stages:
  - stage: 0x10
    tasks: []
    checks: []
```

returns both:

```text
stages[0].stage: 'stage' must be a string
stages[0].stage: stage identifier is required
```

The second message is false: the field is present, but has an invalid type.
The same cascade occurs for numeric/boolean task or check `id` and `uses`
values. This violates the issue's requirement for a complete, actionable
domain-language error list and makes a single type mistake look like two
independent authoring mistakes. Structural/type failures must be marked as
unavailable to the semantic pass, or semantic validation must suppress the
derived required check for paths already carrying a type error.

<promise>FAIL</promise>
