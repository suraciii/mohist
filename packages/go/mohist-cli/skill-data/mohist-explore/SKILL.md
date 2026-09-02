---
name: mohist-explore
description: Distill requirement input at any maturity into a clear, bounded requirement clarification for Mohist issues. Use before creating issues or epics.
---

# mohist-explore

Use this skill to distill requirement input into a clear, bounded requirement
clarification for a Mohist issue.

The output is a requirement clarification clear enough for
`mohist-create-issue` to fill an issue template against. This skill is about
thinking clearly; it does not define issue-body sections or touch the CLI.

## Thinking lenses

- **User Voice:** What does the user need, where do they get stuck, and when
  does this matter?
- **Product Shape:** What is experienced today, what is the target product form,
  and what is explicitly out of scope?
- **Domain Model:** What concepts, invariants, and constraints shape the
  decision? Skip this for simple technical changes.

Work through the lenses in order and ask only unanswered questions. Confirm the
captured need before moving on. If a later lens reveals a contradiction, revise
the earlier lens explicitly.

## Scope

Every issue must have standalone value. Split different problems or bounded
contexts. For each proposed issue, state its one-sentence value, ensure every
scope item serves it, and apply the stop-here test. Record real dependencies and
the suggested start order when there are multiple issues.

## Handoff

Present the clarified requirement for user confirmation before anything is
created. Then use `mohist-create-issue` for a single issue or
`mohist-create-epic` for a milestone with multiple issues.
