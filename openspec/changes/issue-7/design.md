## Context

Task artifact expectations and check verdict validation currently share marker-oriented language. That makes a failed check verdict, such as a review artifact containing `<promise>FAIL</promise>` when a check expects `<promise>PASS</promise>`, look like a task artifact marker failure. The proposal narrows task expectations to artifact completion requirements and keeps PASS/FAIL semantics in check definitions.

The affected stakeholders are workflow profile authors, runner/runtime maintainers, and users reading workflow failure diagnostics. The change should preserve the workflow stage model and existing artifact production behavior while clarifying the declarative contract and error messages. No prompt, API, storage, or dependency changes are expected.

## Goals / Non-Goals

**Goals:**

- Rename or reshape task expectation concepts so they describe required artifact files and optional neutral artifact markers/content.
- Ensure built-in workflow profiles do not model PASS/FAIL verdicts as task artifact completion requirements.
- Keep verdict marker requirements on check definitions and evaluate them only in check execution paths.
- Produce distinct diagnostics for missing artifact files, missing neutral artifact markers, and missing or mismatched check verdict markers.
- Add tests that cover task file requirements, neutral task artifact markers, and check PASS marker validation as separate behaviors.

**Non-Goals:**

- Do not change agent prompts unless implementation reveals a contract mismatch that cannot be solved in definitions/runtime code.
- Do not repair existing e2e-smoke issue state or rewrite historical artifacts.
- Do not change workflow stage transitions, approval behavior, storage format, public API shape, or runner process orchestration.

## Decisions

1. Treat task expectations as artifact requirements, not verdict checks.

   Task definitions should expose artifact-focused names such as required files and optional artifact markers/content. Runtime validation should verify file existence and, when configured, neutral content markers inside those files. It should not interpret PASS, FAIL, `<promise>PASS</promise>`, or `<promise>FAIL</promise>` as task success semantics.

   Alternative considered: keep the generic marker schema and document that task markers must not be verdict markers. This keeps code churn lower but leaves the domain ambiguity in the type names, profile definitions, and diagnostics. The issue is primarily about separating domain concepts, so explicit artifact terminology is preferred.

2. Keep PASS/FAIL marker requirements exclusively on checks.

   Check definitions remain responsible for declaring required verdict evidence, such as `PASS` or `<promise>PASS</promise>`. The check execution path should load the relevant artifact, evaluate the expected verdict marker, and report a check verdict failure when evidence is missing or mismatched.

   Alternative considered: derive check verdict requirements from task artifact expectations. This would preserve the current coupling and continue to make producing an artifact and passing a check indistinguishable, so it is rejected.

3. Use domain-specific diagnostics at the validation boundary.

   Task artifact validation should report messages in terms of missing artifact files or missing artifact markers. Check validation should report messages in terms of check verdict evidence, including the check id/name and expected marker. Shared lower-level marker utilities may still exist, but their raw errors should be wrapped or translated before reaching runner-facing diagnostics.

   Alternative considered: change only the low-level marker utility message. That would improve wording in one place but still fail to identify whether the caller was validating an artifact contract or a verdict contract. Translating errors at the task/check boundary gives the runner enough domain context.

4. Update built-in workflow profiles rather than prompts.

   Built-in profiles should remove PASS/FAIL verdict markers from task artifact expectations and keep those requirements under check definitions. This directly fixes the modeling issue without asking agents to produce different files.

   Alternative considered: prompt agents to always end review artifacts with a PASS marker. That would mask failed reviews and contradict the requirement that failed verdicts belong to check validation, not task completion.

5. Test the two validation paths independently.

   Add or update tests so task validation covers missing files and neutral artifact markers, while check validation separately covers required PASS markers and failed verdict artifacts. Include a regression where `review.md` exists with `<promise>FAIL</promise>` and the failure is reported as the `review-passed` check verdict, not as an `ai-review` task artifact marker failure.

   Alternative considered: rely on an end-to-end workflow test only. E2E coverage is useful, but the distinction is a domain contract and should be enforced by focused tests that make the two paths hard to accidentally merge again.

## Risks / Trade-offs

- [Risk] Existing internal profile data may still use old marker field names. -> Mitigation: update all built-in profiles in the same change and, if parser compatibility is needed for checked-in definitions, keep migration localized to definition loading rather than runtime validation semantics.
- [Risk] Rejecting PASS/FAIL markers in task artifact expectations could break a custom profile that relied on the old ambiguous behavior. -> Mitigation: fail fast with an artifact-schema diagnostic that tells authors to move verdict markers into a check definition.
- [Risk] Shared marker utility errors may leak through unwrapped and keep the old confusing wording. -> Mitigation: add tests against runner-facing task and check failure messages, not only utility return values.
- [Risk] Neutral artifact markers and verdict markers are both string containment checks, so future code may collapse them again. -> Mitigation: use separate type names, validator entry points, and test names for artifact marker validation and check verdict validation.

## Migration Plan

1. Update task expectation types/schema names to artifact-focused terminology.
2. Update built-in workflow profiles so tasks require artifact files and optional neutral artifact markers only.
3. Keep or introduce check definition fields for required verdict markers and route PASS/FAIL validation through check execution.
4. Translate low-level marker failures into task artifact diagnostics or check verdict diagnostics at their respective validation boundaries.
5. Add focused regression tests for artifact file validation, neutral artifact marker validation, and check PASS marker validation.
6. Run the relevant server/runner tests and a full build if practical.

Rollback is code-level only: revert the schema/profile/runtime changes together. There is no expected persistent data migration. If backward-compatible definition loading is added, rollback should also remove that compatibility path to avoid keeping the old ambiguous contract alive.

## Open Questions

- Should task definition loading explicitly reject PASS/FAIL-like artifact markers, or only avoid using them in built-in profiles and runtime semantics?
- Are there external/custom workflow profiles that need a short compatibility window for old task marker field names?
- What exact diagnostic text should become the stable contract for runner-facing artifact failures and verdict failures?
