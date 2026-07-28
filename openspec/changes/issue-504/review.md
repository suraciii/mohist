## Findings

### P2: Include Unicode whitespace in legacy context validation

`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260728000000_TypedWorkflowRunLineage.cs:63` defines `Whitespace` as only ASCII whitespace code points. The old reader used .NET `int.TryParse` and `string.IsNullOrWhiteSpace`, which also recognize Unicode whitespace such as U+00A0 (non-breaking space). As a result, a legacy `"\u00A042\u00A0"` Issue number that the old reader accepts is left annotation-backed after upgrade, while a U+00A0-only Project ID can still be migrated as a non-empty typed Project. This leaves behavior dependent on which whitespace character was stored and can again produce Runs whose new lineage path is unusable. Extend the SQL trim character set to match the accepted .NET whitespace set, and add U+00A0-padded Issue and Project migration cases.

<promise>FAIL</promise>
