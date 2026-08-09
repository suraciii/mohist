# Issue Templates

An Issue template defines the shape of an Issue body. It is independent of the
selected WorkflowProfile, which defines execution. Template asset files are the
authority for section-level writing instructions, prohibited content, and
conditions for removing an optional section:
`packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/templates/{feature,bug,refactor}.md`.
This document records rationale without copying that content.

## Metadata Contains Only Name and Description

Each template front matter has two fields:

| Field | Purpose |
|---|---|
| `name` | Display name, such as Feature |
| `description` | One-sentence description and the sole basis for template selection |

- There is no `suitableFor`, `defaults`, or explicit ID. The file name is the
  ID: `feature.md` becomes `feature`.
- A person or Agent reads `description` to select a template. The program does
  not match it. Descriptions must distinguish the three templates through the
  central question of whether external behavior changes.
- Workflow and risk do not belong in template metadata. They are Issue front
  matter recommended separately by the create-Issue Skill.

## Two-stage Loading with Agent-side Selection

Templates serve Web users and the `mohist-create-issue` Skill through the same
three-step interaction:

| Step | Person in Web creation | Agent using `mohist-create-issue` |
|---|---|---|
| 1. Read catalog | `GET /issue-templates`, metadata only | `mo issue template list`, metadata only |
| 2. Select | Person reads the list | Agent reads description; no programmatic matching |
| 3. Read complete template | `GET /issue-templates/{id}` returns body | `mo issue template view <id>` returns body with inline instructions |

Selection happens in the person or Agent from metadata. Load the body only
after selection. Discovery never reads it, matching Skill discovery:

| Phase | Trigger | Read | Output |
|---|---|---|---|
| Discovery | List and select | Front matter only | Name and description |
| Detail | Get and compose body | Front matter plus complete raw body | Unmodified body string including HTML comments |

## Body Is Opaque Raw Text

Server treats the body as an opaque raw string. It does not parse sections,
extract guidance, or remove HTML comments. This matches classic GitHub Markdown
Issue templates, which insert the complete body including `<!-- comments -->`.

- Web prefills the editor with the body, and CLI displays it unchanged. Neither
  removes comments. Markdown rendering hides comments from the final view.
- Section-level writing instructions remain HTML comments in the body and reach
  the person or Agent that fills the template.

## Three Issue Types

Every Mohist Issue produces code and integrates it through Plan, Build, Check,
and Integrate. There is no decision-only lane. Three types are sufficient:

| Type | Meaning | Criterion |
|---|---|---|
| **Feature** | New product behavior or an iteration on existing behavior | A user can observe a change in external behavior |
| **Bug** | Repair of incorrect functional or nonfunctional behavior | A correct-state invariant was violated |
| **Refactor** | Internal quality work such as restructuring, coverage, or optimization | External behavior remains unchanged; value is maintainability, reliability, or headroom |

The deciding question is: Does external behavior change? A change is Feature.
No change while repairing an incorrect condition is Bug. No change while
improving already correct internals is Refactor.

An iteration on existing behavior is Feature, not Refactor. Refactor is defined
by preserved external behavior.

## The Primary Consumer Is an Agent Planner

This distinguishes Mohist templates from generic GitHub Issue templates. A
person asks follow-up questions about phrases such as "somewhat slow" or
"somewhat disorganized." A Plan Agent can accept ambiguity and produce an
ambiguous Plan.

Every section must therefore be actionable for planning: reproducible,
observable, and measurable. The create-Issue Skill states this common rule
once. Each template's HTML comments state section-specific prohibitions, such
as vague Bug symptoms or subjective Refactor completion.

## Industry Practice Mapping

| Template | Practices used |
|---|---|
| Feature | Agile User Story and INVEST; Dual-Track Agile or Opportunity-Solution Tree; optional BDD or Gherkin acceptance |
| Bug | ISTQB defect reporting with symptom, reproduction, expected versus actual, and severity; DORA and SPACE for measurable nonfunctional defects; a violated invariant from DDD |
| Refactor | Fowler's behavior-preserving definition; characterization test or golden master; Definition of Done with preserved behavior and improved structure |

## Shared Structure

All templates use five sections. Only the middle semantics differ: Feature
creates behavior, Bug corrects behavior, and Refactor preserves behavior.

| Section | Feature | Bug | Refactor |
|---|---|---|---|
| 1. Why | **User Voice** | **Symptom and Evidence**, two states | **Motivation** |
| 2. Shape | **Product Shape** | **Fix Shape** | **Change Scope** |
| 3. Domain core | **Domain Model**, optional | **Domain Context**, required | **Behavior Contract**, required |
| 4. Acceptance | **Acceptance Criteria** | **Acceptance Criteria** | **Done When** |
| 5. Boundary | **Non-goals** | **Non-goals** | **Non-goals** to prevent scope growth |

One structure reduces cognitive load for people and Agents. Template selection
changes only the meaning of the middle sections.

> Severity, Priority, and Risk are Issue front-matter fields and must not be
> repeated in the body. The body contains only semantics needed by the planner.

## Implementation Gaps

- Custom template CRUD does not exist. `ProjectIssueTemplates` has a read side
  but no write path. When CRUD is added, migrate storage from legacy
  `{sections:[...]}` to `{body:"..."}` and remove the compatibility path.
- The Web selector does not show `description`.
