# Self-Review: Issue 513

Reviewed `proposal.md`, `design.md`, `tasks.json`, and all four specs under
`specs/` against issue 513 (让 Agent 输入支持明确附件). Acting as reviewer only;
no artifact other than this file was modified.

## Acceptance-criteria coverage

All seven issue ACs are covered by spec requirements and tasked:

| AC | Covered by | Task |
|---|---|---|
| Web + CLI attach at launch/follow-up, see pending before submit | `agent-attachment-entry` req 1–2 | T-004, T-005 |
| Attachment-only (no text) input accepted, builds SessionInput + AgentTurn, no fabricated prompt | `session-input-attachments` req 1; `agent-attachment-delivery` req 1 | T-002, T-003 |
| Accepted attachment shows name/type/size/source; Agent reads actual content | `attachment-input-lifecycle` req 1; `agent-attachment-delivery` req 1 | T-001, T-003 |
| Missing/unreadable/oversized/unsupported surfaced specifically; failed not given to Agent | `session-input-attachments` req 3–4 | T-002 |
| Only owning SessionInput; no cross-session/user/Connection reuse by reference | `attachment-input-lifecycle` req 2–3; `session-input-attachments` req 5 | T-001, T-002 |
| Temp URL/credentials/raw events never enter Instructions/reply/transcript | `attachment-input-lifecycle` req 5; `agent-attachment-delivery` req 3 | T-001, T-003 |
| Unified retention/cleanup, no effect on persisted work/results | `attachment-input-lifecycle` req 4 | T-001 |

## Cross-artifact consistency

- Proposal's four capabilities ↔ exactly four spec dirs ↔ five tasks mapping
  1:1 to capabilities (entry split Web/CLI). DAG is valid and acyclic:
  `T-001 → T-002 → T-003 → {T-004, T-005}`, every `dependsOn` points to a
  strictly lower priority.
- Current-state claims in proposal/design are accurate against the codebase
  (`AgentSessionInputRecord.Text`-only, `AllowedTopLevelFields`, `prompt_required`/
  `followup_text_missing`, text-only dispatch, broken inline `att:` Web path,
  shared `AttachmentService` + `CleanupExpiredPendingAsync` keyed on
  `OwnerKind == null`).
- Specs are well-formed: every requirement has ≥1 `#### Scenario`, normative
  SHALL/MUST language, no `ADDED/MODIFIED/REMOVED` headers, no cross-spec
  references.

## Findings (non-blocking)

These are clarifications/symmetry improvements; none blocks building, and each
is already resolved or bounded elsewhere in the plan.

### N1. The "no fabricated prompt" contract is resolved in design, not spelled out in the spec

The most delicate requirement — attachment-only turns must not get a fabricated
prompt — is unambiguously resolved in design D4 + Risks: a **visible,
system-attributed, factual manifest** of attached files is permitted as
turn-initiating content; inventing user intent is forbidden. The spec wording
(`session-input-attachments` req 1 forbids a *hidden* prompt; `agent-attachment-delivery`
req 1 forbids *fabricating a prompt*) is consistent with this once "prompt" is
read as user-intent text, but the spec never explicitly blesses the manifest
mechanism. An implementer reading only the spec could over-read "no prompt" and
block attachment-only turns on runtimes that require non-empty turn text. The
design removes that risk. **Recommendation:** add one scenario to
`agent-attachment-delivery` stating that a factual, system-attributed manifest
of attached files is an acceptable turn input while invented user task text is
not. Not a blocker — the design is authoritative on the "how."

### N2. API field name `attachments` vs `attachmentIds` is a bounded open question

Proposal/design/tasks use `attachments`; the codebase convention (issue path)
and T-004's "reuse `extractAttachmentIds`" lean toward `attachmentIds`. This is
already listed under design Open Questions, so it is bounded and documented.
Pick one at implementation (prefer `attachmentIds` for codebase consistency).

### N3. Task ACs are slightly narrower than the spec on metadata

`attachment-input-lifecycle` req 1 requires observation of source **and
availability**; T-001's AC lists "name/content-type/size/source" (omits
"availability"), and neither T-001 nor T-002 explicitly calls out updating the
`AgentSessionInputObservationDto` read model. The spec is authoritative and
covers both, so nothing is missing — the implementer should treat the spec as
the source of truth for required metadata and ensure the observation surface
exposes it.

### N4. T-001 is a caller-less foundation until T-002

T-001 (agent-input owner kind + bind + scoped content route + retention) has no
production caller until T-002 wires acceptance to it. This is a valid
abstraction-layer split (explicitly permitted) and its ACs are independently
testable; noting only so it is not mistaken for a standalone deliverable.

## Verdict

The plan is complete (all 7 ACs covered), internally consistent, grounded in
accurate current-state facts, and the delicate design points are documented as
decisions/risks/bounded open questions rather than hidden gaps. The findings
above are clarifications; none prevents an autonomous implementer from building
the correct, complete feature from these four artifacts. Ready to build.

<promise>PASS</promise>
