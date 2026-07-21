### Requirement: Row labels describe their data
In the Details card, each metadata row's label MUST describe the data shown in that row. The parent-issue reference row and the child-issues row MUST NOT share the same label.

#### Scenario: Parent reference row is labeled as a parent reference
- **WHEN** the issue has a parent issue reference
- **THEN** the Details card MUST render a row whose label identifies it as the parent reference, linking to the parent issue

#### Scenario: Child-issues row is labeled distinctly from the parent reference
- **WHEN** the issue is itself a parent (has child issues)
- **THEN** the Details card MUST render a row describing the issue's parent status or children using a label distinct from the parent reference row label, and MUST NOT label the child-issues row "Parent Issue"
