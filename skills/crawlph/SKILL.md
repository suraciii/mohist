---
name: crawlph
description: |
  Spec-driven development workflow automation with Ralph Loop.
  
  Usage: /crawlph [issue-number] [--watch] [--cron] [--dry-run] [--yes] [--label LABEL] [--limit N] [--stage STAGE] [--notify-channel CHANNEL] [--timeout MINUTES]
  
  Examples:
    /crawlph                    # Process all pending Issues interactively
    /crawlph 123                # Process specific Issue #123
    /crawlph --watch            # Continuously watch for new Issues (poll every 60s)
    /crawlph --cron             # Single run for cron/scheduled execution
    /crawlph --dry-run          # Preview what would be processed without making changes
    /crawlph --yes              # Skip confirmation prompts
    /crawlph --stage exploration  # Only process Issues in exploration stage
    /crawlph --label "bug" --limit 5  # Process up to 5 Issues labeled "bug"
    /crawlph --notify-channel "telegram:123456"  # Send progress to specific Telegram channel

user-invocable: true
metadata:
  { 
    "openclaw": { 
      "requires": { 
        "bins": ["curl", "git", "gh"] 
      }, 
      "primaryEnv": "GH_TOKEN",
      "install": "OpenSpec CLI >= v1.0.0 (optional, for spec generation)"
    } 
  }
---

# crawlph — Spec-Driven Development Workflow Automation

You are an orchestrator agent that automates the spec-driven development workflow from GitHub Issues to Production. Follow these phases exactly. Do not skip phases.

IMPORTANT: 
- You manage Issue processing through a 7-stage workflow (Exploration → Refinement → Design → Implementation → Review → Done → Re-evaluation)
- You implement Ralph Loop: infinite retry with clean context per attempt
- Design and Implementation happen in the SAME PR (Draft → Open → Merged)
- You spawn sub-agents via `sessions_spawn` with `runtime: "acp"` and `agentId: "opencode"`
- You persist state in `~/.openclaw/agents/crawlph/data/` directory
- You send progress notifications via Channel AND record milestones in Issue Comments
- Maximum 8 concurrent sub-agents

---

## Phase 1 — Parse Arguments

Parse the arguments string to determine execution mode and filters.

**Positional Arguments:**
- `issue-number` — Optional. Specific Issue number to process. If omitted, process all pending Issues.

**Flags:**

| Flag | Default | Description |
|------|---------|-------------|
| `--watch` | false | Continuously poll for new Issues every 60 seconds |
| `--cron` | false | Single run mode for scheduled execution (exit after completion) |
| `--dry-run` | false | Preview what would be processed without making changes |
| `--yes` | false | Skip confirmation prompts |
| `--label` | _(none)_ | Filter Issues by label (e.g., "bug", "feature") |
| `--limit` | 10 | Maximum number of Issues to process in one run |
| `--stage` | _(none)_ | Filter by workflow stage (exploration, refinement, design, implementation, review, done, blocked) |
| `--notify-channel` | _(from config)_ | Override notification channel (e.g., "telegram:123456") |
| `--timeout` | 30 | Sub-agent timeout in minutes |

**Derived Values:**
- `WATCH_MODE = true` if `--watch` flag is set
- `CRON_MODE = true` if `--cron` flag is set
- `DRY_RUN = true` if `--dry-run` flag is set
- `AUTO_CONFIRM = true` if `--yes` flag is set
- `NOTIFY_CHANNEL = --notify-channel value OR skills.entries["crawlph"].notifyChannel from config OR null`
- `MAX_CONCURRENT = 8` (hardcoded limit)
- `POLL_INTERVAL = 60` seconds (for watch mode)

**If `--watch` and `--cron` both set:**
> Error: Cannot use --watch and --cron together. Choose one mode.

Store parsed values for use in subsequent phases.

---

## Phase 2 — Fetch Issues

Fetch Issues from GitHub that need processing.

**2.1. Determine Repository:**

```bash
REPO=$(git remote get-url origin | sed -E 's|.*github.com[/:]||; s|\.git$||')
```

If not in a git repository or no remote named "origin":
> Error: Not in a git repository or no 'origin' remote. Run this command from a git repository.

**2.2. Build GitHub API Query:**

Determine which Issues to fetch based on arguments:

- If `issue-number` provided: Fetch that specific Issue
- Otherwise: Fetch open Issues filtered by `--label` and `--stage`

**Query parameters:**
```bash
GH_ARGS="state:open"
[ -n "$LABEL" ] && GH_ARGS="$GH_ARGS label:$LABEL"
[ -n "$STAGE" ] && GH_ARGS="$GH_ARGS label:stage:$STAGE"
GH_ARGS="$GH_ARGS repo:$REPO"
```

**2.3. Fetch Issues:**

```bash
ISSUES_JSON=$(gh issue list --search "$GH_ARGS" --limit $LIMIT --json number,title,labels,body,updatedAt)
```

If empty result:
> No Issues found matching criteria. Nothing to process.

**2.4. Load Claims:**

Read `~/.openclaw/agents/crawlph/data/crawlph-claims.json` to get currently claimed Issues:

```json
{
  "claims": {
    "123": {
      "claimedAt": "2024-01-01T00:00:00Z",
      "agentId": "session-abc"
    }
  }
}
```

**2.5. Filter Claimed Issues:**

Remove Issues that are already claimed by another session:

- If Issue is in `claims` and claim is less than 24 hours old → skip (already being processed)
- If Issue is in `claims` but claim is older than 24 hours → remove stale claim and include

Store filtered Issues for next phase.

---

## Phase 3 — Present & Confirm

Present the list of Issues to process and get user confirmation.

**3.1. Display Issues Table:**

```
Issues to process:

| #   | Title                              | Stage        | Updated    |
|-----|------------------------------------|--------------|------------|
| 123 | Add authentication feature         | exploration  | 2024-01-01 |
| 124 | Fix login bug                      | refinement   | 2024-01-02 |

Total: 2 Issues
```

**3.2. Check Mode:**

- If `DRY_RUN = true`:
  > Dry-run mode. Would process N Issues. Exiting.
  - Stop here, do not proceed to Phase 4.

- If `AUTO_CONFIRM = true`:
  - Skip confirmation, proceed directly to Phase 4.

- Otherwise:
  - Ask user: "Process these N Issues? (yes/no)"
  - If user says "no" or "n": Stop here.
  - If user says "yes" or "y": Proceed to Phase 4.

---

## Phase 4 — Process Issues (Ralph Loop)

Process each Issue using the Ralph Loop pattern (infinite retry until success).

**4.1. Initialize Processing:**

For each Issue to process:

1. **Claim the Issue:**
   - Add entry to `~/.openclaw/agents/crawlph/data/crawlph-claims.json`
   - Set `claimedAt` to current timestamp
   - Set `agentId` to current session ID

2. **Initialize Progress File:**
   - Create `~/.openclaw/agents/crawlph/data/progress/issue-{N}.json`
   ```json
   {
     "issueNumber": 123,
     "currentStage": "exploration",
     "attempts": 0,
     "prNumber": null,
     "lastError": null,
     "checkpoints": {},
     "context": {
       "branchName": null,
       "specFile": null,
       "usesOpenSpec": null
     }
   }
   ```

**4.2. Ralph Loop (per Issue):**

```
while (true) {
  # Read current progress
  progress = read_progress_file(issue_number)
  
  # Spawn sub-agent with clean context
  result = spawn_sub_agent({
    issue_number,
    current_stage: progress.current_stage,
    checkpoints: progress.checkpoints
  })
  
  # Handle result
  if (result.status === SUCCESS) {
    cleanup_progress_file(issue_number)
    release_claim(issue_number)
    break  # Exit loop, Issue processed successfully
  } else if (result.status === NEEDS_USER_INPUT) {
    send_channel_notification("Issue #{issue_number} needs user input")
    # Pause and wait for user action
    # User can comment on Issue or add label
    wait_for_user_action()
  } else {
    # Failure - update progress and retry
    progress.attempts += 1
    progress.last_error = result.error
    write_progress_file(issue_number, progress)
    send_channel_notification("Issue #{issue_number} retry attempt #{progress.attempts}")
    
    # Check for persistent failure
    if (progress.attempts >= 10) {
      add_label(issue_number, "stage:blocked")
      send_channel_notification("Issue #{issue_number} marked as blocked after 10 failures")
      release_claim(issue_number)
      break  # Exit loop, needs manual intervention
    }
  }
}
```

**4.3. Sub-agent Template:**

For each Issue, spawn a sub-agent with these instructions:

<config>
Repository: {REPO}
Issue Number: {ISSUE_NUMBER}
Current Stage: {CURRENT_STAGE}
Checkpoints: {CHECKPOINTS}
Branch Name: issue-{ISSUE_NUMBER}-{short-title}
</config>

<issue>
Number: {ISSUE_NUMBER}
Title: {ISSUE_TITLE}
Body: {ISSUE_BODY}
Labels: {ISSUE_LABELS}
</issue>

<instructions>
You are a sub-agent processing a GitHub Issue through a 7-stage workflow. Follow these stages in order. Mark each stage complete by adding a comment to the Issue.

**Stage 1: Exploration (label: stage:exploration)**
- Analyze the Issue to understand the requirement
- Explore the codebase to find relevant files and patterns
- Identify integration points and potential challenges
- Add exploratory comment to Issue summarizing findings
- Do NOT make any code changes yet

**Stage 2: Refinement (label: stage:refinement)**
- Clarify requirements if ambiguous (ask questions in Issue comments)
- Complete the Issue body with detailed task list (use checkboxes: `- [ ] task`)
- Ensure requirements are testable and unambiguous
- Wait for user confirmation before proceeding to Design
- Trigger: User says "可以设计了" OR Issue has ≥ 2 checkbox tasks

**Stage 3: Design (label: stage:design)**
- Generate design specifications:
  - If OpenSpec CLI available (check with `openspec --version`): run `openspec propose issue-{ISSUE_NUMBER}`
  - Otherwise: manually create `specs/issue-{ISSUE_NUMBER}.md` with Why, What Changes, Capabilities, Impact
- Create a Draft PR:
  - Branch: `issue-{ISSUE_NUMBER}-{short-title}` (from default branch)
  - Add specs files to PR
  - PR body: "Closes #{ISSUE_NUMBER}\n\n**OpenSpec**: [Used/Not Used]"
  - If not using OpenSpec, add: "**注意**: 未使用 OpenSpec 格式，specs 手动生成"
- Add Issue comment: "Design complete. Draft PR: [link]"

**Stage 4: Implementation (label: stage:implementation)**
- Implement the feature/fix based on the design specs
- Commit changes to the same PR branch
- Ensure code follows existing patterns and conventions
- Run tests and linting (if applicable)
- When implementation complete: Convert PR from Draft to Open ("Ready for Review")
- Add Issue comment: "Implementation complete. Ready for review: [PR link]"

**Stage 5: Review (label: stage:review)**
- Perform automated review:
  - Check code quality, test coverage, documentation
  - Post review comments on PR if issues found
- Wait for user review and approval
- Address review feedback if any
- When approved: Proceed to Stage 6

**Stage 6: Done (label: stage:done)**
- Merge the PR (use squash merge)
- Close the Issue
- Add final comment: "Merged in [PR link]. Issue closed."

**Stage 7: Re-evaluation (label: stage:reevaluation) — OPTIONAL**
- Only triggered manually by user
- Re-analyze if the implementation meets requirements
- Make adjustments if needed

</instructions>

<constraints>
- Do NOT force-push to any branch
- Do NOT make changes unrelated to the Issue
- Do NOT skip stages
- Time limit: {TIMEOUT} minutes
- Clean context: This is a fresh attempt, do not rely on previous context
- One PR for both design and implementation
- If OpenSpec not available, document in PR body
</constraints>

**4.4. Spawn Configuration:**

```yaml
spawn_config:
  runtime: "acp"
  agentId: "opencode"
  runTimeoutSeconds: {TIMEOUT * 60}
  cleanup: "keep"  # Preserve transcripts for debugging
```

**4.5. Concurrent Processing:**

Process up to 8 Issues concurrently:
- Track active sub-agents
- When one completes, start processing next Issue
- Wait for all to complete before Phase 5

---

## Phase 5 — Results Collection

Collect and aggregate results from all processed Issues.

**5.1. Summary Table:**

```
Processing Results:

| Issue | Status  | Stage Reached | PR    | Notes              |
|-------|---------|---------------|-------|--------------------|
| 123   | ✅ Done  | done          | #45   | Merged successfully |
| 124   | ⏸ Blocked| implementation | #46  | Needs user input    |
| 125   | ✅ Done  | done          | #47   | Merged successfully |

Total: 3 Issues processed
  - 2 completed successfully
  - 1 blocked (requires manual intervention)
```

**5.2. Update Labels:**

For each processed Issue:
- Remove `stage:exploration/refinement/design/implementation/review` labels
- Add appropriate final label (`stage:done` or `stage:blocked`)

**5.3. Send Summary Notification:**

If `NOTIFY_CHANNEL` is set, send summary to channel:
```
crawlph completed processing run:
- 2 Issues completed successfully
- 1 Issue blocked

Details:
- #123: ✅ Merged
- #124: ⏸ Blocked (needs user input)
- #125: ✅ Merged
```

---

## Phase 6 — Watch/Cron Mode Handling

Handle continuous operation modes.

**If `WATCH_MODE = true`:**

```bash
# Context Hygiene: Clear transient data
Keep only:
- PROCESSED_ISSUES (set of Issue numbers)
- OPEN_PRS (list of PR numbers)
- FAILED_ISSUES (map of Issue to failure count)
- Configuration parameters

Clear:
- Issue bodies
- Comment bodies
- Sub-agent transcripts
- Codebase analysis results

# Save cursor for next iteration
echo '{"lastRun": "'$(date -Iseconds)'"}' > ~/.openclaw/agents/crawlph/data/crawlph-cursor.json

# Sleep and loop back to Phase 2
sleep 60
# Loop back to Phase 2
```

**If `CRON_MODE = true`:**
- Exit after Phase 5 completes
- No looping

**If neither watch nor cron:**
- Return to interactive mode
- Wait for next user command

---

## Phase 7 — Cleanup & Recovery

Handle edge cases and cleanup.

**7.1. Stale Claim Cleanup:**

On startup, check `~/.openclaw/agents/crawlph/data/crawlph-claims.json`:
- Remove claims older than 24 hours
- These are likely from crashed sessions

**7.2. Corrupted State Handling:**

If progress file is corrupted:
- Log error
- Remove corrupted file
- Treat as fresh Issue (start from Stage 1)

**7.3. Recovery After Restart:**

If Orchestrator restarts:
   - Read all progress files in `~/.openclaw/agents/crawlph/data/progress/`
- Resume processing from last known stage
- Do NOT restart from Stage 1 unless progress file missing

---

## Error Handling

**GitHub API Errors:**
- If rate limited: Wait and retry with exponential backoff
- If authentication error: Stop and tell user to check GH_TOKEN

**Sub-agent Timeout:**
- If sub-agent exceeds timeout: Treat as failure
- Update progress file
- Trigger Ralph Loop retry

**Network Errors:**
- Retry with exponential backoff
- After 3 failures: Send notification and pause

**File System Errors:**
- If cannot write to `~/.openclaw/agents/crawlph/data/`: Stop and tell user to check permissions
- Use atomic writes (write to temp file, then rename)

---

## Configuration

Users can configure default behavior in `~/.openclaw/openclaw.json`:

```json
{
  "skills": {
    "entries": {
      "crawlph": {
        "notifyChannel": "telegram:123456",
        "defaultTimeout": 30,
        "pollInterval": 60
      }
    }
  }
}
```

---

## Examples

**Interactive Mode:**
```
User: /crawlph
Bot: Issues to process:
     | #   | Title                    | Stage       |
     |-----|--------------------------|-------------|
     | 123 | Add authentication       | exploration |
     
     Process these 1 Issues? (yes/no)
User: yes
Bot: [Processing Issue #123...]
     [Spawned sub-agent for Issue #123]
     [... sub-agent works through stages ...]
     ✅ Issue #123 completed successfully
```

**Watch Mode:**
```
User: /crawlph --watch
Bot: Starting watch mode. Polling every 60 seconds.
     [Checks for new Issues every 60s]
     [Processes Issues as they appear]
     [Continues until manually stopped]
```

**Dry Run:**
```
User: /crawlph --dry-run --label bug
Bot: Would process 3 Issues with label "bug":
     | #   | Title                    |
     |-----|--------------------------|
     | 120 | Fix login crash          |
     | 121 | Handle null pointer      |
     | 122 | Validate input           |
     
     Dry-run mode. Exiting.
```
