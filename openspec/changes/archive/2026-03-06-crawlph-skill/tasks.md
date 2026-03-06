## 1. Project Setup

- [x] 1.1 Create `skills/crawlph/` directory structure
- [x] 1.2 Create `skills/crawlph/SKILL.md` with basic metadata (name, description, user-invocable)
- [x] 1.3 Create `~/.openclaw/agents/crawlph/data/` directory for state storage

## 2. Issue Orchestration

- [x] 2.1 Implement Phase 1: Parse Arguments (--watch, --cron, --dry-run, --yes, --label, --limit, --stage)
- [x] 2.2 Implement Phase 2: Fetch Issues (GitHub API, filter by stage:* labels)
- [x] 2.3 Implement claim-based tracking (crawlph-claims.json)
- [x] 2.4 Implement Phase 3: Present & Confirm (display Issues list, user confirmation)
- [x] 2.5 Implement concurrent sub-agent spawning (max 8)
- [x] 2.6 Implement Phase 5: Results Collection and aggregation
- [x] 2.7 Implement --watch mode (continuous polling with interval)
- [x] 2.8 Implement --cron mode (single run, exit after completion)
- [x] 2.9 Implement Context hygiene (retain only essential state)

## 3. Ralph Loop

- [x] 3.1 Implement Orchestrator infinite retry loop
- [x] 3.2 Implement sub-agent spawning with clean context
- [x] 3.3 Implement progress persistence between attempts
- [x] 3.4 Implement failure detection (consecutive failures > 10)
- [x] 3.5 Implement warning notification on persistent failure
- [x] 3.6 Implement stage:blocked label handling (skip processing)

## 4. Workflow Stages

- [x] 4.1 Implement Stage 1: Exploration (analyze Issue, explore codebase)
- [x] 4.2 Implement Stage 2: Refinement (clarify requirements, complete requirements)
- [x] 4.3 Implement Stage 3: Design (generate specs, create Draft PR)
- [x] 4.4 Implement "可以设计了" trigger detection
- [x] 4.5 Implement Stage 4: Implementation (implement based on specs, commit to PR)
- [x] 4.6 Implement PR state transition (Draft → Open)
- [x] 4.7 Implement Stage 5: Review (automated review, wait for user review)
- [x] 4.8 Implement review comment handling
- [x] 4.9 Implement Stage 6: Done (merge PR, close Issue)
- [x] 4.10 Implement Stage 7: Re-evaluation (manual trigger only)

## 5. OpenSpec Integration

- [x] 5.1 Implement OpenSpec CLI availability check
- [x] 5.2 Implement version verification (minimum required version)
- [x] 5.3 Implement spec generation via `openspec propose`
- [x] 5.4 Implement fallback to manual specs (specs/issue-{N}.md)
- [x] 5.5 Implement PR body documentation for non-OpenSpec format

## 6. PR Lifecycle

- [x] 6.1 Implement Draft PR creation in design stage
- [x] 6.2 Implement branch naming convention (issue-{N}-{short-description})
- [x] 6.3 Implement Draft → Open transition
- [x] 6.4 Implement PR merge on approval (squash merge)
- [x] 6.5 Implement PR description template (Issue reference, spec format note)
- [x] 6.6 Implement PR cleanup on failure (close PR, delete branch)

## 7. Progress Reporting

- [x] 7.1 Implement Telegram channel notification support
- [x] 7.2 Implement channel configuration (skills.entries["crawlph"].notifyChannel)
- [x] 7.3 Implement --notify-channel command line parameter
- [x] 7.4 Implement stage transition notifications
- [x] 7.5 Implement completion notifications (Issue number, PR link, summary)
- [x] 7.6 Implement failure notifications (Issue number, error, retry count)
- [x] 7.7 Implement notification throttling (max 1 per minute per Issue)

## 8. State Persistence

- [x] 8.1 Implement crawlph-claims.json file format
- [x] 8.2 Implement crawlph-cursor.json file format
- [x] 8.3 Implement crawlph-progress/issue-{N}.json file format
- [x] 8.4 Implement atomic file writes (write to temp, then rename)
- [x] 8.5 Implement state recovery after restart
- [x] 8.6 Implement corrupted state handling
- [x] 8.7 Implement stale claim cleanup (24 hour threshold)

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
- [x] 9.11 Test state persistence and recovery
- [ ] 9.12 Write SKILL.md documentation (usage, configuration, examples)
