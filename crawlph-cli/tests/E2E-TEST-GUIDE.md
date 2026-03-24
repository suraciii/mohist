# End-to-End Test Guide

This document describes the complete E2E test flow for crawlph.

## Prerequisites

1. GitHub repository with Issues
2. GitHub token with repo permissions
3. opencode CLI installed and configured
4. crawlph server running

## Test Scenario: Single Issue Complete Flow

### Step 1: Setup

```bash
# Start the server
crawlph server start

# Verify server is running
crawlph server status

# Create a test project
crawlph project create test-project --repo owner/repo

# Verify project created
crawlph project list
```

### Step 2: Create Test Issue

```bash
# Create a test issue on GitHub
gh issue create --title "Test Issue for E2E" --body "This is a test issue"

# Verify issue appears in crawlph
crawlph issue list
```

### Step 3: Start Processing

```bash
# Start processing the issue
crawlph issue start 1

# Verify issue moved to designing stage
crawlph issue show 1

# Check server logs for agent execution
crawlph server logs
```

### Step 4: Design Review Checkpoint

```bash
# Wait for designing stage to complete
# Issue should be in waiting-design-review stage

# Review the PR
crawlph pr show 1
crawlph pr review 1  # Opens in browser

# Approve the design
crawlph pr approve 1 --message "Design looks good"
```

### Step 5: Implementation

```bash
# Issue should automatically move to implementing stage
crawlph issue show 1

# Wait for implementation to complete
# Issue should be in waiting-review stage
```

### Step 6: Final Review

```bash
# Review the implementation
crawlph pr review 1

# Approve the implementation
crawlph pr approve 1 --message "Implementation approved"
```

### Step 7: Merge

```bash
# Issue should automatically move to merging stage
crawlph issue show 1

# Wait for merge to complete
# Issue should be in done stage
```

### Step 8: Verification

```bash
# Verify issue is done
crawlph issue show 1

# Verify PR is merged
crawlph pr show 1

# Check server logs for complete flow
crawlph server logs -n 100
```

## Expected Results

1. Issue flows through all stages: draft → designing → waiting-design-review → implementing → waiting-review → merging → done
2. PR is created and updated at each stage
3. User approval is required at design and implementation checkpoints
4. PR is merged automatically after final approval
5. Issue is marked as done

## Test Variations

### Pause/Resume Test

```bash
# Start processing
crawlph issue start 2

# Pause during execution
crawlph issue pause 2

# Verify issue is paused
crawlph issue show 2

# Resume processing
crawlph issue resume 2

# Verify issue continues
crawlph issue show 2
```

### Multi-Issue Concurrent Test

```bash
# Start multiple issues
crawlph issue start 3
crawlph issue start 4
crawlph issue start 5

# Verify concurrent execution (max 8)
crawlph status

# Check all are being processed
crawlph issue list
```

### Error Handling Test

```bash
# Create issue with invalid configuration
# Verify error is caught and issue marked as blocked

# Check logs for error details
crawlph server logs

# Fix issue and resume
crawlph issue resume 6
```

## Cleanup

```bash
# Remove test project
crawlph project remove test-project

# Stop server
crawlph server stop

# Verify server stopped
crawlph server status
```

## Success Criteria

- All stages transition correctly
- User approval checkpoints work
- PR creation and updates work
- Pause/resume functionality works
- Concurrent execution respects limits
- Error handling works correctly
- Server can be started and stopped cleanly
- Logs capture all important events
