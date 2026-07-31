## Why

`mo otel query` currently opens the local `otel.db` itself, bypassing the Server query safety net and silently querying local data even when the CLI targets a remote Server. The CLI must use the authoritative query surface now that it provides bounded execution and results, so every user receives the same safe, target-correct diagnostics.

## What Changes

- Route `mo otel query <sql>` through the Server's `POST /otel/api/query` capability instead of directly opening a local SQLite database.
- Render the Server query result, including bounded-result truncation and its reason, for both human-readable and field-selected JSON output.
- Add supported `--json` field selection for query results so Agents can request only the rows and query metadata they need.
- Remove the local database path selection and direct-database behavior from `mo otel query`; Server unavailability becomes an actionable command failure, while direct storage inspection remains a developer-only path outside the CLI product surface.

## Capabilities

- `otel-cli-query`: The CLI executes OTel SQL through the Server query contract, presents complete or explicitly truncated results consistently to people and Agents, supports field-selected JSON output, and never substitutes local storage for the configured Server target.

## Impact

- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Otel.cs` and CLI specs): replace the SQLite executor and `--db` path handling with the Server query client, result rendering, error handling, and JSON field contract.
- **Server API** (`POST /otel/api/query`): becomes the required execution surface for CLI query requests; its existing SELECT-only admission, read-only database access, execution budget, response budgets, and truncation response are consumed without creating a second query policy.
- **Documentation** (`docs/cli-reference.md`): remove the recorded implementation gap once the CLI behavior matches the documented command contract.
- **Dependencies and persistence**: remove the CLI's direct SQLite query dependency and make no persistence-schema change.
