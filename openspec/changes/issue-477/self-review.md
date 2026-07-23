# Self Review: Issue 477

Reviewed the current issue, proposal, design, task breakdown, and all three capability specs.

## Findings

1. **[BLOCKER] The plan leaves newly created Projects without a defined default Profile or startup behavior.**
   `design.md` defines Run selection solely as `issue.workflowProfileId ?? project.defaultWorkflowProfileId` (lines 66-75), and removes the legacy cascade/fallback (lines 130-145). The migration only converts existing Project defaults (lines 177-184); neither the design, task breakdown, nor selection spec defines how a Project created after rollout gets its initial `defaultWorkflowProfileId`, nor the required behavior if it remains null. Current behavior has a system-profile fallback for Projects without a configured default. As written, a new Issue without an explicit selection has no Profile to bind and cannot start, despite every Project exposing built-ins. Define and test the creation-time default (or an intentional, documented start-time rejection and its CLI/API error) before implementation.

2. **[BLOCKER] The stated delete/reference revalidation does not prevent the dangling-reference race it claims to close.**
   `design.md` lines 104-111 says deletion checks references and that delete, Project/Issue selection, and Run creation each revalidate collection existence, then concludes that no dangling reference can remain. Two independently committed operations can still interleave: a selection or Run creation validates an existing Profile, deletion observes no reference and deletes it, then the reference owner commits. The proposed per-owner validation alone therefore cannot meet the deletion-protection invariant, particularly because built-ins prevent relying on a simple database foreign key. Specify one atomic serialization/fencing mechanism shared by Profile deletion and every reference-writing path, including its conflict/retry result, and add a deterministic concurrency spec for the interleaving.

<promise>FAIL</promise>
