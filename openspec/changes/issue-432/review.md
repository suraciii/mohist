# Review Findings

## P1: Profile save still truncates multi-document YAML

`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileYamlParser.cs:11-30`
loads the input into a `YamlStream`, reads only `stream.Documents[0]`, removes
Profile metadata from that first root, and serializes only that root before
calling the authoritative parser. The parser now rejects multiple documents,
but this wrapper prevents that check from seeing them. A Profile payload with a
valid first document followed by `---` and an invalid/unknown second document
is therefore accepted by the save path while the same Definition is rejected by
`mo workflow validate --file`.

The save entry point must reject `stream.Documents.Count > 1` before extracting
metadata, or otherwise pass the complete input to the shared parser. Otherwise
the acceptance criterion for rejecting illegal structures and keeping save/CLI
validation consistent is still unmet.

## P1: A legacy public parser remains an alternate, non-authoritative path

`packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:74-104`
still exposes `FromYaml` and `FromProfileYaml`, backed by a deserializer created
with `IgnoreUnmatchedProperties()` at lines 119-122. Its parsing code also
coerces values such as recovery `budget` through `int.TryParse` and defaults
invalid values, and it keeps separate structural/Action checks. Although the
current save managers use `WorkflowProfileYamlParser`, the old public class is
still product code and is actively exercised by Server tests and helper paths.
It can therefore accept a Definition that the issue's authoritative parser
rejects, violating the design requirement to remove the superseded parser
paths and the single semantic-model/validator boundary.

Remove the old YAML parsing entry points or make them strict adapters over
`WorkflowDefinitionParser` (and retain only serialization helpers that do not
parse user-authored Definitions). Update the remaining tests/helpers to use the
shared parser so no second Definition rule set remains callable.

## P1: Multiple template errors at one YAML path are silently dropped

`packages/server/src/Mohist.Workflow.Definition/WorkflowDefinitionParser.cs:857-867`
and the corresponding `AddError` in
`packages/server/src/Mohist.Workflow.Definition/WorkflowDefinitionRules.cs:797-808`
deduplicate solely by `Path`. The template walk reports every expression using
the containing scalar's path, so a value such as:

```yaml
prompt: "${{ bogus.x }} ${{ failure.x }}"
```

in an ordinary task produces only the `bogus` error; the forbidden
`failure.*` error is discarded because both use the same
`...with.prompt` path. The CLI currently demonstrates this by printing one
line for the two invalid expressions. This contradicts the requirement to
collect the complete error list and makes the reported result depend on
expression order.

Deduplicate only identical error instances (for example, the same path and
message emitted by structural and semantic passes), or add expression
location/identity to the key, while retaining all distinct template reasons at
the same YAML path.

<promise>FAIL</promise>
