## Findings

### P1: Preserve tab- and newline-padded legacy Issue numbers

`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260728000000_TypedWorkflowRunLineage.cs:101` uses SQLite's no-argument `trim()`, which removes ASCII spaces but not tabs or newlines. The preceding reader used `int.TryParse`, whose default integer style accepts leading and trailing whitespace, including `"\t42\t"`. The current `NOT GLOB '*[^0-9]*'` condition then rejects that otherwise valid legacy value, leaving the old annotations in a Run that the new code no longer reads; ownership and event lineage fail after reload. Trim the same accepted whitespace set before optional-sign/digit validation in both migration branches, and add tab/newline-padded migration fixtures alongside the existing space-padded one.

<promise>FAIL</promise>
