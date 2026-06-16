## Context

Today, when the explore skill concludes an exploration, findings are written as free-form Markdown into issue bodies via `mo issue create --body-file`. There is no structured linkage to workflow selection — the user must manually specify `--workflow-profile` or accept the default. This leads to inconsistent issue quality and missed opportunities to route issues to the optimal workflow.

The CLI already supports `--workflow-profile` and sends `workflowProfileId` to the `POST /api/issues` endpoint (which accepts it via `CreateIssueRequest`). What's missing is the *discovery and recommendation* layer: extracting a workflow recommendation from the issue body itself.

The `mohist-explore` skill (packaged in `skill-data/mohist-explore/SKILL.md`) currently lacks instructions for structured body production. The `mo workflow list` command exists but lacks `--described` output for rich metadata. The Issue model has no `risk` field.

## Goals / Non-Goals

**Goals:**
- Define a YAML frontmatter convention (`recommended_workflow`, `recommended_workflow_reason`, `risk`) for issue body files
- `mo issue create --body-file` parses frontmatter and auto-fills `--workflow-profile` and risk, with CLI flags taking precedence
- `mohist-explore` skill guidance instructs agents to produce frontmatter-annotated body files
- Web UI create-issue dialog detects frontmatter in body text and displays workflow recommendation
- `mo workflow list --described` emits workflow IDs with descriptions and `suitable_for` metadata

**Non-Goals:**
- ML-based workflow recommendation — rule-based matching only
- Explore skill dialogue capability improvements
- Enforcing frontmatter presence — it remains advisory
- Adding `suitable_for` metadata to workflow profiles (prerequisite, handled separately)

## Decisions

### D1: Frontmatter is parsed in the CLI, not the server

The CLI's `BodyInputResolver.ResolveAsync()` already reads the body file. We extend the flow: after resolving the body text, a new `FrontmatterParser` class extracts YAML frontmatter. The parsed values are used as defaults for `--workflow-profile` and `--risk`, but explicit CLI flags override them.

**Rationale**: The CLI is a thin client that passes structured fields to the server. Parsing frontmatter in the CLI avoids API changes to accept raw frontmatter strings. The server receives clean, typed `WorkflowProfileId` and `risk` fields regardless of whether they came from flags or frontmatter.

**Alternative considered**: Server-side parsing of the body field. Rejected because it mixes concerns (the body is user content, not an API contract) and requires the server to be aware of a client-side convention.

### D2: Frontmatter parsing uses a simple line-scanning approach, not a full YAML library

Frontmatter is delimited by `---` at the start of the file, with simple key-value pairs inside. We scan lines between the first two `---` markers and split on `:`, trimming whitespace. Multi-line values (like `recommended_workflow_reason`) are supported via YAML block scalar syntax (indented continuation or `|`).

**Rationale**: Avoids adding a YAML library dependency to the CLI. The frontmatter schema is intentionally minimal (3 fields). Full YAML parsing is overkill for 3 flat keys, and malformed YAML is handled gracefully (treat entire file as body).

**Alternative considered**: Using `YamlDotNet` or similar. Rejected to keep the CLI dependency-free for this feature and to maintain graceful degradation on malformed input.

### D3: `risk` is a new field on the Issue model, persisted server-side

The `risk` value is stored as a nullable string on the issue record. It flows through `CreateIssueRequest` and is persisted by `IssueGrain.CreateAsync()`. The `Issue` TypeScript type and `IssueReadModel` both gain `risk?: string | null`.

**Rationale**: Risk is a first-class issue attribute, not just parsed metadata. Storing it makes it queryable and visible in both CLI (`mo issue show`) and Web UI (issue detail, kanban cards). The frontmatter is the *source* at creation time; thereafter the canonical value lives in the database.

### D4: Web UI frontmatter detection is client-side

The create-issue dialog watches the body `Textarea` value. When the value starts with `---`, a `useMemo` extracts frontmatter keys and surfaces the recommendation above the workflow selector. No additional API call is needed.

**Rationale**: Instant feedback. The body text is already in the browser. Server round-trips to parse frontmatter add latency for no gain.

### D5: `mo workflow list --described` uses a new server endpoint

The existing `mo workflow list` calls `GET /api/config/list` or similar. A new `--described` flag maps to a new endpoint (e.g., `GET /api/workflow-profiles`) that returns each profile's `id`, `displayName`, `description`, and `suitableFor` array.

**Rationale**: The described output is richer than the current list format. A dedicated endpoint lets the server compose the response from `IssueWorkflowProfileRegistry` entries. The `suitable_for` metadata is a prerequisite that must be added to each `IIssueWorkflowProfile` implementation.

### D6: Skill content is updated in the packaged skill-data, version-matched

The `skill-data/mohist-explore/SKILL.md` is the source of truth served by `mo skills get mohist-explore`. We update it with the frontmatter production workflow. The repository copy at `.agents/skills/mohist-explore/SKILL.md` is a discovery stub — it does not need updating.

**Rationale**: Discovered via exploration — `mo skills install` writes stubs; `mo skills get` serves the packaged content. Changing the packaged source ensures all users get the updated guidance regardless of when they last ran `mo skills install`.

## Risks / Trade-offs

- **[Risk] Frontmatter in body files could conflict with legitimate `---` content in Markdown** → Mitigation: Only the *first* `---`-delimited block is treated as frontmatter. Files without a leading `---` are unaffected. Users who genuinely need `---` at line 1 can prepend a blank line.
- **[Risk] `risk` field adds a new DB column requiring migration** → Mitigation: Use a nullable column with default NULL. All existing issues get NULL risk. Migration is backward-compatible (no data loss, no schema break).
- **[Risk] Explore skill may pick wrong workflow if `suitable_for` metadata is absent or vague** → Mitigation: Default to `mohist/default` when no match is found. The user always sees the recommendation before confirming.
- **[Risk] `suitable_for` is a prerequisite dependency** → Mitigation: The frontmatter parsing and CLI changes work independently. The `--described` flag and skill matching logic are gated on the prerequisite being done. Without it, the skill falls back to recommending `mohist/default`.

## Migration Plan

1. **Deploy schema migration**: Add nullable `risk` column to issues table. Existing issues get NULL. No downtime.
2. **Deploy server changes**: `CreateIssueRequest` gains `Risk` field. `POST /api/issues` persists it. `GET /api/issues` returns it.
3. **Deploy CLI changes**: `mos issue create --body-file` gains frontmatter parsing. New `--risk` flag. `mo workflow list --described` added.
4. **Deploy Web UI changes**: Create-issue dialog detects frontmatter. Issue detail shows risk.
5. **Deploy skill content**: Update `skill-data/mohist-explore/SKILL.md`. Users get new content on next `mo skills install`.

**Rollback**: Each component is independent. CLI can roll back to previous version (frontmatter is silently ignored, body still works). Server risk column is nullable — rollback removes the read/write logic but leaves the column harmlessly. Web UI changes are additive UI.

## Open Questions

1. **Should `risk` accept arbitrary values or be constrained to `low/medium/high`?** Current spec says `low`, `medium`, `high`. Decision: validate server-side, but use CLI-side suggestion only. The issue body already documents the convention.
2. **Should `mo workflow list` output include builtin vs. project vs. issue-level profiles?** Out of scope for this issue — `--described` focuses on `id`, `description`, and `suitableFor`.
3. **Should the explore skill produce a `body.md` file automatically or let the user choose the path?** The skill produces to a temp path and suggests the `mo issue create` command. User can redirect.
