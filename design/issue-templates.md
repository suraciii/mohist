# Issue Templates

An Issue template defines the shape of an Issue body. It is independent of the
selected Workflow Profile, which defines execution. Template asset files are
the authority for section writing instructions, prohibited content, and rules
for removing optional sections:
`packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/templates/{feature,bug,refactor}.md`.

## Design Drivers

- The same metadata catalog serves Web users and the `mohist-create-issue`
  Skill.
- A person or Agent selects a template from its description. The program does
  not match descriptions.
- Server returns the selected body as opaque raw text, including HTML comments.
- Every body section must give a Plan Agent reproducible, observable, and
  measurable input.
- Issue metadata carries Workflow and risk separately from the template body.

## Model

### Metadata Contains Only Name and Description

Each template front matter has exactly two fields:

- `name` is the display name, such as `Feature`.
- `description` is one sentence and is the sole basis for selection.

The file name is the template ID. For example, `feature.md` has ID `feature`.
There is no `suitableFor`, `defaults`, or explicit ID field. Descriptions must
distinguish the three templates by whether external behavior changes.
Workflow and risk do not belong in template metadata. The create-Issue Skill
recommends those Issue front-matter fields separately.

### Three Issue Types

Every Mohist Issue produces code and enters Plan, Build, Check, and Integrate.
There is no decision-only lane. Three types are sufficient:

- **Feature** adds or changes observable product behavior.
- **Bug** repairs incorrect functional or nonfunctional behavior by restoring a
  violated invariant.
- **Refactor** improves internal quality while external behavior remains
  unchanged. Its value is maintainability, reliability, or headroom.

The deciding question is whether external behavior changes. A change is a
Feature. Repair without a behavior change is a Bug. Internal improvement of
already-correct behavior is a Refactor. An iteration on existing behavior is a
Feature, not a Refactor.

### Shared Structure

All templates use five sections. Only the middle semantics differ: Feature
creates behavior, Bug corrects behavior, and Refactor preserves behavior.

1. **Why**: User Voice for Feature, Symptom and Evidence for Bug, or Motivation
   for Refactor.
2. **Shape**: Product Shape for Feature, Fix Shape for Bug, or Change Scope for
   Refactor.
3. **Domain core**: optional Domain Model for Feature, required Domain Context
   for Bug, or required Behavior Contract for Refactor.
4. **Acceptance**: Acceptance Criteria for Feature and Bug, or Done When for
   Refactor.
5. **Boundary**: Non-goals for every type. Refactor non-goals prevent scope
   growth.

Severity, Priority, and Risk are Issue front-matter fields. They must not be
repeated in the body.

## Semantics

### Two-stage Loading with Agent-side Selection

Template access follows this flow:

```text diagram
+------------------+    +-------------------------+    +------------------------+
| Catalog metadata +--->| Person or Agent selects +--->| Load complete template |
+------------------+    +-------------------------+    +------------------------+
```

Template access uses three steps:

1. **Catalog**: `GET /issue-templates` or `mo issue template list` returns
   metadata only.
2. **Selection**: a person reads the list, and an Agent reads `description`.
   No programmatic matching occurs.
3. **Detail**: `GET /issue-templates/{id}` or
   `mo issue template view <id>` returns front matter and the complete raw body.

Discovery reads front matter only and outputs `name` and `description`. Detail
reads front matter plus the unmodified body, including HTML comments.

### Body Is Opaque Raw Text

Server does not parse sections, extract guidance, or remove HTML comments. This
matches classic GitHub Markdown Issue templates, which insert the complete body.
Web prefills the editor with the body, and CLI displays it unchanged. Markdown
rendering hides comments from the final view. Section writing instructions
remain in comments and reach the person or Agent that fills the template.

### Planning Contract

The primary consumer is an Agent planner. Each section must be actionable for
planning: reproducible, observable, and measurable. The create-Issue Skill
states this shared rule once. Template comments add section-specific rules,
such as rejecting vague Bug symptoms or subjective Refactor completion.

## Status

- Custom template CRUD does not exist. `ProjectIssueTemplates` has a read side
  but no write path.
- The Web selector does not show `description`.
