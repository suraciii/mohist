# Self Review: Issue 477

Reviewed the current issue, proposal, design, task breakdown, and all three capability specs.

## Findings

1. **[BLOCKER] The claimed Issue-selection/delete race protection is not serializable.** `design.md` lines 133-140 and the collection spec's deletion-order scenarios assert that a deletion blocker query plus the Issue participant's existence revalidation closes the race without cross-coordinator synchronization. It does not: an Issue selection can validate that a Profile exists after the deletion query has read no Issue reference, then commit after the Profile deletion; conversely, it can validate before the deletion and commit after it. The Issue selection and deletion have separate coordinators and separate aggregate transactions, so neither revalidation establishes a shared ordering with the delete. This can leave an Issue referencing a deleted Profile, violating the issue's deletion-safety criterion. Define an atomic/shared serialization or durable protocol for Issue selection and deletion that preserves the existing Issue-create repository-binding invariant, and add deterministic specs covering the check/validate/commit interleavings rather than only whole-request orderings.

2. **[BLOCKER] Deletion protection is narrowed to non-terminal Issues, contrary to the issued requirement.** The issue requires rejection while a Profile is referenced by an "Issue" and the collection spec repeats this as "any Issue" (`specs/workflow-profile-collection/spec.md` line 53). However, `design.md` line 128 and T-001 acceptance criterion 16 query only non-terminal Issue explicit selections. A terminal Issue can therefore retain a migrated or explicit Profile ID while that Profile is deleted, violating the required reference protection and breaking the stable reference model. Protect references from all Issues, or obtain an explicit issue/spec change that limits the requirement to non-terminal Issues and defines the required historical-reference behavior.

<promise>FAIL</promise>
