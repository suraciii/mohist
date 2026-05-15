## Context

The session transcript UI currently has a split tool rendering model. `ToolCallCard.tsx` contains legacy parsing helpers for JSON inputs, labels, argument badges, display type selection, and patch parsing, while the newer `session-transcript/tool-registry.tsx` path delegates that same knowledge to `lib/transcript-tool-utils.ts`.

This change is a maintenance refactor, not a transcript UX redesign. The existing card layout, raw input/output disclosures, terminal output, and file-change summaries should continue to behave as they do today, but the semantic decisions behind those cards should come from the same shared utilities and registry path used by the newer transcript components.

## Goals / Non-Goals

**Goals:**

- Remove duplicated tool parsing functions from `ToolCallCard.tsx`.
- Make legacy card rendering use the same label, args, display type, and patch/file-change parsing rules as `session-transcript/tool-registry.tsx`.
- Keep the visible transcript behavior compatible for existing known and unknown tools.
- Make future tool display changes local to `transcript-tool-utils.ts` or `tool-registry.tsx`.

**Non-Goals:**

- Redesigning the session transcript layout or interaction model.
- Adding a new diff viewer or changing file-change rendering capability.
- Changing backend transcript payloads, APIs, persistence, or normalization.
- Removing `ToolCallCard` entirely if doing so would force a larger UI rewrite.

## Decisions

### D1: Keep `ToolCallCard` as a presentation component, but remove parsing ownership

`ToolCallCard` should import shared parsing helpers from `lib/transcript-tool-utils.ts` instead of defining local copies. The card can keep visual concerns such as status icons, expansion state, raw payload formatting, terminal rendering, and compact rendering, but it must not own tool semantic rules.

**Alternatives considered:** Replace all `ToolCallCard` usage with the newer transcript part components. This would remove the legacy component more aggressively, but it would couple a maintenance refactor to a larger UI migration and increase the chance of changing transcript behavior.

### D2: Use registry-facing helpers for tool identity and display classification

Display type selection should flow through the shared `getDisplayType`/registry path rather than a local `TOOL_DISPLAY_TYPE` map. Label and badge text should flow through `getToolLabel` and `getToolArgs`, and file-changing tools should use the shared patch/edit parsing helpers already consumed by `tool-registry.tsx`.

**Alternatives considered:** Move the duplicated code from `ToolCallCard` into a new helper file. That would reduce the component size but would still leave a parallel helper surface beside `transcript-tool-utils.ts`, preserving the drift risk.

### D3: Share icons only where it does not disrupt the current card API

If practical, `ToolCallCard` can use `getToolIcon` or registry metadata for generic icons, but icon consolidation is secondary to removing parsing duplication. The important contract is that semantic parsing rules have one source of truth; local visual wrappers may remain if they are presentation-only.

**Alternatives considered:** Force every visual property through the registry. This would be cleaner long-term, but the current registry returns React elements designed for the newer transcript components and may require extra adaptation for legacy class names.

### D4: Prefer compatibility checks over broad snapshot updates

Verification should focus on build/typecheck and any existing frontend tests that cover transcript rendering. If targeted tests are added, they should assert shared helper usage outcomes for representative tools such as `bash`, `read`, `grep`, `apply_patch`, `edit`, `write`, and unknown tools, not brittle DOM snapshots of styling.

**Alternatives considered:** Add comprehensive UI snapshots for every card variant. This would increase test maintenance cost without directly protecting the refactor's main risk: semantic drift between parser paths.

## Risks / Trade-offs

- [Risk] Shared helpers may not preserve every legacy edge case in `ToolCallCard` exactly. → Mitigation: compare behavior for known tool families before removing local helpers, and only centralize rules that already exist in `transcript-tool-utils.ts`.
- [Risk] Importing registry helpers into `ToolCallCard` could create circular dependencies if presentation components are pulled into utility code. → Mitigation: keep shared non-React parsing in `lib/transcript-tool-utils.ts`; only import registry metadata from component code when needed.
- [Risk] A full replacement of `ToolCallCard` could unintentionally change transcript layout. → Mitigation: keep this change scoped to parsing and semantic metadata ownership, leaving existing card rendering structure intact.
- [Risk] Unknown tool fallback behavior could become less informative. → Mitigation: preserve fallback label and args behavior through shared `getToolLabel`, `getToolArgs`, and `getFallbackSubtitle` where applicable.

## Migration Plan

1. Update `ToolCallCard.tsx` to import shared parsing helpers and remove local duplicates for `parseJsonSafely`, `getToolLabel`, `getToolArgs`, `TOOL_DISPLAY_TYPE`, and `parsePatchOperations`.
2. Replace local display-type lookup with the shared `getDisplayType` or registry-facing `getToolDisplayType` path.
3. Replace local edit/write file summary parsing with shared `parseEditInput` and `parseEditWriteChanges` where possible.
4. Keep existing `SessionTranscriptView.tsx` call sites intact unless a small adapter is needed to pass the same `ToolCallEntry` shape.
5. Run the relevant frontend build/typecheck and existing tests.
6. Rollback is a normal code revert; no data migration or backend compatibility step is required.

## Open Questions

- Should icon metadata be fully centralized in `tool-registry.tsx` during this refactor, or left as presentation-only duplication until a broader registry cleanup?
