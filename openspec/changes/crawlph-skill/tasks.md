## 1. Project Setup

- [ ] 1.1 Create `skills/crawlph/` directory structure
- [ ] 1.2 Create `skills/crawlph/SKILL.md` with basic metadata (name, description, user-invocable)
- [ ] 1.3 Create `/data/.clawdbot/` directory for state storage

## 2. Issue Orchestration

- [ ] 2.1 Implement Phase 1: Parse Arguments (--watch, --cron, --dry-run, --yes, --label, --limit, --stage)
- [ ] 2.2 Implement Phase 2: Fetch Issues (GitHub API, filter by stage:* labels)
- [ ] 2.3 Implement claim-based tracking (crawlph-claims.json)
- [ ] 2.4 Implement Phase 3: Present & Confirm (display Issues list, user confirmation)
- [ ] 2.5 Implement concurrent sub-agent spawning (max 8)
- [ ] 2.6 Implement Phase 5: Results Collection and aggregation
- [ ] 2.7 Implement --watch mode (continuous polling with interval)
- [ ] 2.8 Implement --cron mode (single run, exit after completion)
- [ ] 2.9 Implement Context hygiene (retain only essential state)

## 3. Ralph Loop

- [ ] 3.1 Implement Orchestrator infinite retry loop
- [ ] 3.2 Implement sub-agent spawning with clean context
- [ ] 3.3 Implement progress persistence between attempts
- [ ] 3.4 Implement failure detection (consecutive failures > 10)
- [ ] 3.5 Implement warning notification on persistent failure
- [ ] 3.6 Implement stage:blocked label handling (skip processing)

## 4. Workflow Stages

- [ ] 4.1 Implement Stage 1: Exploration (analyze Issue, explore codebase)
- [ ] 4.2 Implement Stage 2: Refinement (clarify requirements, complete requirements)
- [ ] 4.3 Implement Stage 3: Design (generate specs, create Draft PR)
- [ ] 4.4 Implement "可以设计了" trigger detection
- [ ] 4.5 Implement Stage 4: Implementation (implement based on specs, commit to PR)
- [ ] 4.6 Implement PR state transition (Draft → Open)
- [ ] 4.7 Implement Stage 5: Review (automated review, wait for user review)
- [ ] 4.8 Implement review comment handling
- [ ] 4.9 Implement Stage 6: Done (merge PR, close Issue)
- [ ] 4.10 Implement Stage 7: Re-evaluation (manual trigger only)

## 5. OpenSpec Integration

- [ ] 5.1 Implement OpenSpec CLI availability check
- [ ] 5.2 Implement version verification (minimum required version)
- [ ] 5.3 Implement spec generation via `openspec propose`
- [ ] 5.4 Implement fallback to manual specs (specs/issue-{N}.md)
- [ ] 5.5 Implement PR body documentation for non-OpenSpec format

## 6. PR Lifecycle

- [ ] 6.1 Implement Draft PR creation in design stage
- [ ] 6.2 Implement branch naming convention (issue-{N}-{short-description})
- [ ] 6.3 Implement Draft → Open transition
- [ ] 6.4 Implement PR merge on approval (squash merge)
- [ ] 6.5 Implement PR description template (Issue reference, spec format note)
- [ ] 6.6 Implement PR cleanup on failure (close PR, delete branch)

## 7. Progress Reporting

- [ ] 7.1 Implement Telegram channel notification support
- [ ] 7.2 Implement channel configuration (skills.entries["crawlph"].notifyChannel)
- [ ] 7.3 Implement --notify-channel command line parameter
- [ ] 7.4 Implement stage transition notifications
- [ ] 7.5 Implement completion notifications (Issue number, PR link, summary)
- [ ] 7.6 Implement failure notifications (Issue number, error, retry count)
- [ ] 7.7 Implement notification throttling (max 1 per minute per Issue)

## 8. State Persistence

- [ ] 8.1 Implement crawlph-claims.json file format
- [ ] 8.2 Implement crawlph-cursor.json file format
- [ ] 8.3 Implement crawlph-progress/issue-{N}.json file format
- [ ] 8.4 Implement atomic file writes (write to temp, then rename)
- [ ] 8.5 Implement state recovery after restart
- [ ] 8.6 Implement corrupted state handling
- [ ] 8.7 Implement stale claim cleanup (24 hour threshold)

## 9. Testing & Documentation

- [ ] 9.1 Test manual trigger mode
- [ ] 9.2 Test --watch mode with multiple Issues
- [ ] 9.3 Test --cron mode
- [ ] 9.4 Test concurrent processing (8 sub-agents)
- [ ] 9.5 Test Ralph Loop retry mechanism
- [ ] 9.6 Test failure detection and warning
- [ ] 9.7 Test OpenSpec integration
- [ ] 9.8 Test manual spec fallback
- [ ] 9.9 Test PR lifecycle (Draft → Open → Merged)
- [ ] 9.10 Test progress notifications
- [ ] 9.11 Test state persistence and recovery
- [ ] 9.12 Write SKILL.md documentation (usage, configuration, examples)
