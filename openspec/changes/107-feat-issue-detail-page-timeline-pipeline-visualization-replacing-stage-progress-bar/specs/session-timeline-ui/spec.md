## MODIFIED Requirements

### Requirement: Pipeline status timeline

The IssueDetailPage SHALL show a pipeline status timeline above SessionTimeline, displaying key events: pipeline start, each round completion with artifact produced, gate status, and any errors.

#### Scenario: Pipeline in plan stage with gate awaiting
- **WHEN** the plan stage completes and is awaiting approval
- **THEN** the timeline shows: "Pipeline started" → "✓ Proposal" → "✓ Specs" → "✓ Design" → "✓ Tasks" → "✓ Self-review" → "⏸ Awaiting approval"

#### Scenario: Timeline replaced by issue-timeline-ui capability
- **WHEN** the implementation uses the new issue-timeline-ui component
- **THEN** this pipeline status timeline requirement is fulfilled by the `IssueTimeline` component in `packages/cli/web/src/components/IssueTimeline.tsx`
- **AND** the horizontal stage progress bar is removed from IssueDetailPage