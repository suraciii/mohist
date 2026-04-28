## ADDED Requirements

### Requirement: Review self-check validates spec compliance content
The review self-check prompt SHALL verify that the review report substantively checks spec compliance, not just format correctness. The self-check SHALL ensure the report references acceptance criteria and verifies implementation against specs.

#### Scenario: Self-check verifies AC coverage in review report
- **WHEN** the self-check runs after review report generation
- **THEN** the self-check SHALL verify:
  - The report has a "### Spec Compliance" section
  - Each acceptance criterion from tasks.json is addressed in the report
  - Findings reference specific spec requirements (not generic statements)
- **AND** if AC coverage is missing, the self-check SHALL rewrite the report

#### Scenario: Self-check detects generic findings without AC reference
- **WHEN** the review report's Spec Compliance section contains only generic statements (e.g. "code looks correct")
- **AND** does not reference specific acceptance criteria
- **THEN** the self-check SHALL flag this as incomplete and rewrite the report

