# Review Findings

## P1 - Partial comment cleanup does not tighten the baseline

`CommentReferenceRules.Ratchet` accepts a file whose current offender count is lower than a nonzero baseline count: it only reports growth (`currentCount > baselineCount`) or a baseline entry when the count becomes zero. For example, a frozen `{ "Api/Foo.cs": 2 }` baseline with one comment removed and the JSON left unchanged produces no violation. The test at `Ratchet_PassesWhenCurrentCountShrinksBelowBaseline` explicitly locks in that incorrect behavior.

This does not meet T-004's acceptance criterion that removing one offender without updating the baseline fails as stale, nor the planned shrink-only ratchet. It allows a partial cleanup to leave an inflated exception allowance, so a later new forbidden reference can occupy the unused slot without failing the ArchTest. Require every nonzero baseline entry to match the current count (or otherwise record per-occurrence identities), update the baseline when an offender is removed, and replace the passing-shrink test with a stale-baseline failure case.

Verification: `dotnet build Mohist.sln`; ArchTests (50); UnitTests (1553); web typecheck; web tests (5145 passed, 1 skipped). The SpecTests command's filter was ignored by the current test platform and the resulting full suite exceeded the 20-minute timeout.

<promise>FAIL</promise>
