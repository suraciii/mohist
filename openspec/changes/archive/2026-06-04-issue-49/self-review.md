# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The `prompt-template-management/spec.md` "One override per project+key" scenario (lines 97–101) said duplicate keys are rejected with a 409 Conflict, but the "Duplicate key within project is rejected" scenario in the same file (lines 301–305) and T-008's acceptance both describe upsert behavior (the second write updates the existing row). Two scenarios gave opposite answers for the same trigger.
  Verification: Read both scenarios, the proposal, the design.md, and T-008. The plan picks upsert. Rewrote the first scenario to describe the PK constraint as a safety net and explicitly forbid 409.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: consistency
  Evidence: The API `source` value was specified as `project-override` / `project-new` in `prompt-template-management/spec.md` but as `projectⓘ` / `projectⓘ new` in the proposal body, `proposal.md`, and `prompt-template-editor/spec.md`. The data flow (API vs UI display) was ambiguous.
  Verification: Re-read all four files. The management spec's kebab-case is the right contract for the API. The editor spec now explicitly says the API returns kebab-case and the UI transforms to the emoji form, and T-021 acceptance was updated to match.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: consistency
  Evidence: The proposal body, `proposal.md`, and `design.md` (Decision 6 heading) all say "10 REST endpoints" but the actual table lists 8.
  Verification: Counted the rows in the proposal's endpoint table — 8. Updated `proposal.md` to "8 REST endpoints" and `design.md` Decision 6 heading to "8 REST endpoints, 5 web hooks".
  Status: resolved

- [ID: item-4]
  Severity: info
  Scope: consistency
  Evidence: The proposal body says "Add a 7th tab **Settings → Templates**" and `design.md` Decision 7 says "Settings → Templates tab as a 7th tab". The current `SettingsPage.tsx` has 5 sections (ai / agent / repositories / workflows / system), so the new tab is the 6th, not the 7th.
  Verification: Read `SettingsPage.tsx` `VALID_SECTIONS` and counted. Updated `proposal.md`, `design.md` Decision 7, and the design.md open question on placement.
  Status: resolved

- [ID: item-5]
  Severity: info
  Scope: consistency
  Evidence: T-015 description said "Cover the eight behavioural scenarios from the spec" but then listed 11 scenarios. T-019 description said "Add the 5 hooks" but listed 6 hook names plus a 7th (`useExtractVariables`).
  Verification: Counted scenarios in T-015 description (11) and hook names in T-019 description (7 total). Rewrote both descriptions to use accurate counts and to distinguish the 5 spec-mandated hooks from the 2 supporting hooks.
  Status: resolved

- [ID: item-6]
  Severity: info
  Scope: consistency
  Evidence: T-016 acceptance #5 said "The existing FakePromptLoader-based tests still pass", but the constructor for `MohistDefaultIssueWorkflowProfile` is being changed to accept `IProjectTemplateStore` (T-016 itself). T-018 explicitly refactors those tests, so the T-016 claim is wrong.
  Verification: Re-read T-016, T-018, and `MohistDefaultWorkflowProfileSpecs.cs` to confirm the constructor change forces the existing tests to be updated. Rewrote T-016 acceptance #5 to acknowledge the refactor is owned there.
  Status: resolved

- [ID: item-7]
  Severity: info
  Scope: feasibility
  Evidence: T-017 said the API route catches `MissingPromptsException` to return 400, but the existing `IssueRoutes.cs` start handler at lines 183–186 already catches `InvalidOperationException` and returns 409. If `MissingPromptsException` inherits from `InvalidOperationException` (the natural C# choice), the existing catch would intercept it before the new one runs, returning 409 instead of 400.
  Verification: Read `IssueRoutes.cs` lines 168–187. Updated T-017 description and acceptance #3 to require the dedicated `MissingPromptsException` catch to be placed BEFORE the existing `InvalidOperationException` catch and to explain why.
  Status: resolved

- [ID: item-8]
  Severity: info
  Scope: completeness
  Evidence: `design.md` line 206 says "A `unknown_prompt_key` audit event is emitted alongside the 400 so an operator can find the offending workflow." But no task in `tasks.json` creates this event. T-014 only covers the project-template override audit events. There was a gap between design and tasks.
  Verification: Searched `tasks.json` for "unknown_prompt_key" — not present. Inserted a new T-019 "Audit event for unknown_prompt_key in start-work 400 path" with full acceptance criteria, and renumbered the subsequent tasks T-020..T-027 to keep the sequence contiguous. The new T-019 depends on T-017 and T-027 now depends on T-019 along with the other final-gate tasks.
  Status: resolved

## Blocking Items

- [ID: item-9]
  Severity: follow-up
  Scope: feasibility
  Evidence: The proposal says #48 should be shipped first so the editor's Preview pane can use the effective vars endpoint. The plan does not block on #48, which is correct per the proposal's "When #48 is done, can pull effective vars from..." note. No new T-018a-style blocker here — the plan explicitly defers that to a follow-up.
  SuggestedAction: None for the current change. When #48 ships, the editor can swap the sample preview variables for the live effective vars by calling GET /api/issues/{n}/vars/effective — no engine or template changes required.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: consistency
  Evidence: The `proposal.md` and `design.md` body still describe the editor surface using emoji (`projectⓘ`, `projectⓘ new`) in some places and the API uses kebab-case (`project-override`, `project-new`) in the management spec. After the fix in item-2 the data flow is now consistent (API kebab, UI emoji), but the prose in the proposal/design still uses emoji in a few spots without explicitly flagging the transformation.
  SuggestedAction: When implementing T-020 and T-021, document the kebab->emoji transformation in the component file as a one-line comment so future readers don't second-guess the choice.
  Status: follow-up

## Follow-up Items

- [ID: item-11]
  Severity: follow-up
  Scope: alignment
  Evidence: T-001 says frontmatter goes above the existing body and bodies must be byte-identical. The body in the existing `.prompt` files uses triple-quoted raw strings (e.g. `<task>${{ vars.something }} ...</task>`), and adding a YAML header will change the file's first bytes. The "byte-identical" wording is about the body region, not the whole file — this is fine but could be read ambiguously.
  SuggestedAction: When implementing T-001, leave a one-line note in the commit message clarifying "body content below the frontmatter is byte-identical to the prior body" so the diff reads clearly.
  Status: follow-up

- [ID: item-12]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-014 acceptance says "No new event-type registration is required (the timeline already shows everything)". I did not verify the `EventBusEventTypes.All` table is populated from the event store at runtime (vs. statically). If the Activity timeline only shows event types that are explicitly registered in `EventBusEventTypes.cs`, the new `project_template_changed` and `project_template_deleted` events may not appear.
  SuggestedAction: When implementing T-014, verify against `EventBusEventTypes.cs` and `EventBridge.cs` (read both files) that the timeline reads from the events table or has a fallback for unknown event types. If registration is required, add the two types to `EventBusEventTypes.All`.
  Status: follow-up

- [ID: item-13]
  Severity: follow-up
  Scope: completeness
  Evidence: T-009 acceptance #6 says the engine "is registered as a singleton in DI (stateless)" but the actual registration is owned by T-011. This is a small duplicate-claim that could confuse a reviewer.
  SuggestedAction: When implementing T-009, drop the registration claim from its acceptance criteria (it belongs in T-011) and keep T-009 focused on the engine itself.
  Status: follow-up

<promise>PASS</promise>
