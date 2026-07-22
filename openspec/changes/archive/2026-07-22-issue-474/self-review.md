## Result

The plan covers all three proposal capabilities with a pure Definition boundary, Profile-owned metadata and unified selection, and Variables limited to Project, Issue, and WorkflowRun resources. T-001 and T-002 are dependency ordered, include focused high-risk test coverage, and preserve the stated non-goals.

The archive default is now marked as initialization-only: it resolves below explicit Project, Issue, and selected-stage values, while an explicit Run write clears the marker and follows the established top-level and stage-overlay precedence. The design and T-002 require coverage for both fallback and explicit replacement, so the original masking risk is addressed.

<promise>PASS</promise>
