# GitHub Labels Documentation

crawlph uses GitHub Labels to track issue state and status. This document describes the label system.

## Stage Labels

Stage labels indicate which stage of the workflow an issue is currently in.

### Label Format

All stage labels follow the format: `crawlph:stage/<stage-name>`

### Available Stages

| Label | Description | Color |
|-------|-------------|-------|
| `crawlph:stage/draft` | Initial state, not yet started | Gray (`#d4c5f9`) |
| `crawlph:stage/designing` | AI agent is creating design document | Blue (`#0075ca`) |
| `crawlph:stage/waiting-design-review` | Waiting for user to review design | Yellow (`#fbca04`) |
| `crawlph:stage/implementing` | AI agent is implementing the design | Cyan (`#1d76db`) |
| `crawlph:stage/waiting-review` | Waiting for user to review implementation | Magenta (`#5319e7`) |
| `crawlph:stage/merging` | PR is being merged | Orange (`#d93f0b`) |
| `crawlph:stage/done` | Issue completed and PR merged | Green (`#0e8a16`) |

## Status Labels

Status labels indicate the operational status of issue processing.

### Label Format

All status labels follow the format: `crawlph:status/<status-name>`

### Available Statuses

| Label | Description | Color |
|-------|-------------|-------|
| `crawlph:status/active` | Issue is being actively processed | Green (`#0e8a16`) |
| `crawlph:status/paused` | Processing paused by user | Yellow (`#fbca04`) |
| `crawlph:status/blocked` | Processing blocked due to error | Red (`#b60205`) |

## Creating Labels

You can create these labels in your GitHub repository using the GitHub UI or CLI:

### Using GitHub CLI

```bash
# Create stage labels
gh label create "crawlph:stage/draft" --color "d4c5f9" --description "Initial state, not yet started"
gh label create "crawlph:stage/designing" --color "0075ca" --description "AI agent is creating design document"
gh label create "crawlph:stage/waiting-design-review" --color "fbca04" --description "Waiting for user to review design"
gh label create "crawlph:stage/implementing" --color "1d76db" --description "AI agent is implementing the design"
gh label create "crawlph:stage/waiting-review" --color "5319e7" --description "Waiting for user to review implementation"
gh label create "crawlph:stage/merging" --color "d93f0b" --description "PR is being merged"
gh label create "crawlph:stage/done" --color "0e8a16" --description "Issue completed and PR merged"

# Create status labels
gh label create "crawlph:status/active" --color "0e8a16" --description "Issue is being actively processed"
gh label create "crawlph:status/paused" --color "fbca04" --description "Processing paused by user"
gh label create "crawlph:status/blocked" --color "b60205" --description "Processing blocked due to error"
```

### Using Script

```bash
#!/bin/bash

# Stage labels
labels=(
  "crawlph:stage/draft:d4c5f9:Initial state, not yet started"
  "crawlph:stage/designing:0075ca:AI agent is creating design document"
  "crawlph:stage/waiting-design-review:fbca04:Waiting for user to review design"
  "crawlph:stage/implementing:1d76db:AI agent is implementing the design"
  "crawlph:stage/waiting-review:5319e7:Waiting for user to review implementation"
  "crawlph:stage/merging:d93f0b:PR is being merged"
  "crawlph:stage/done:0e8a16:Issue completed and PR merged"
  "crawlph:status/active:0e8a16:Issue is being actively processed"
  "crawlph:status/paused:fbca04:Processing paused by user"
  "crawlph:status/blocked:b60205:Processing blocked due to error"
)

for label in "${labels[@]}"; do
  IFS=':' read -r name color description <<< "$label"
  gh label create "$name" --color "$color" --description "$description" || echo "Label $name already exists"
done
```

## Label Transitions

### Normal Flow

1. Issue created → `crawlph:stage/draft` + `crawlph:status/active`
2. Start processing → `crawlph:stage/designing`
3. Design complete → `crawlph:stage/waiting-design-review`
4. Design approved → `crawlph:stage/implementing`
5. Implementation complete → `crawlph:stage/waiting-review`
6. Implementation approved → `crawlph:stage/merging`
7. PR merged → `crawlph:stage/done`

### User Actions

- **Pause**: Add `crawlph:status/paused`, remove `crawlph:status/active`
- **Resume**: Remove `crawlph:status/paused`, add `crawlph:status/active`
- **Error**: Add `crawlph:status/blocked`, remove `crawlph:status/active`

## Querying by Labels

You can use GitHub's search to filter issues by labels:

```bash
# Find all issues in designing stage
gh issue list --label "crawlph:stage/designing"

# Find all active issues
gh issue list --label "crawlph:status/active"

# Find all blocked issues
gh issue list --label "crawlph:status/blocked"
```

## Label Best Practices

1. **One stage at a time**: An issue should only have one stage label
2. **Status is optional**: Only add status labels when needed (active/paused/blocked)
3. **Clean up old labels**: Remove old stage labels when transitioning
4. **Use colors consistently**: Stick to the color scheme for visual clarity

## Troubleshooting

### Issue not showing up in crawlph

- Ensure the issue has a `crawlph:stage/*` label
- Check that the issue is in the current project's repository

### Wrong stage displayed

- Check the labels on the issue
- Only one `crawlph:stage/*` label should be present
- Use `crawlph issue show <number>` to see current state

### Status not updating

- The poller runs every 60 seconds by default
- Check server logs for errors: `crawlph server logs`
- Verify GitHub token has write permissions
