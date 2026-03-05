# Agent Instructions

## Project Overview

This is crawlph - an AI-powered GitHub workflow automation skill for OpenClaw. It implements the Ralph Loop pattern for infinite retry Issue processing through 6 workflow stages (exploration → refinement → design → implementation → review → done).

## Key Files

| File | Purpose |
|------|---------|
| `skills/crawlph/SKILL.md` | Main skill definition and workflow logic |
| `openspec/changes/crawlph-skill/` | Change artifacts (design, specs, tests) |
| `AGENT-TEST-GUIDE.md` | **Testing guide for all changes** |

## Before You Start

**Read the testing guide**: `AGENT-TEST-GUIDE.md`

This guide is a general reference for testing any changes in this project.

This is essential before modifying:
- SKILL.md behavior or workflow logic
- State persistence (claims, progress, cursor files)
- Sub-agent spawn configuration
- Path handling or data directory logic

## Testing

### Quick Test

```bash
# Set environment
export GH_TOKEN=$(gh auth token)
export CRAWLPH_DATA_DIR="$HOME/.openclaw/agents/crawlph-test/data"

# Run test
cd /mnt/c/Users/szf/repos/crawlph-test
timeout 180 openclaw agent --agent crawlph-test --local --message '/crawlph 1 --yes' --timeout 170
```

### Verification Checklist

After test execution, verify:
1. **Claims**: `cat ~/.openclaw/agents/crawlph-test/data/crawlph-claims.json`
2. **Progress**: `ls ~/.openclaw/agents/crawlph-test/data/progress/`
3. **Issue status**: `gh issue view 1`
4. **PR status**: `gh pr list`
5. **Git log**: `git log --oneline -5`

### Common Issues

| Issue | Solution |
|-------|----------|
| API rate limit | `export GH_TOKEN=$(gh auth token)` |
| Wrong data directory | Verify `CRAWLPH_DATA_DIR` is set |
| Agent not found | Run `setup_test_agent.py` |

## Commands

```bash
# Check skill syntax
ls skills/crawlph/

# View test execution logs
cat openspec/changes/crawlph-skill/TEST-EXECUTION-LOG.md

# Monitor running agent
tail -f /tmp/test-run.log

# Clean test data
rm -rf ~/.openclaw/agents/crawlph-test/data/progress/*
```

### Validation Repository Management
- Test issues should be created in `crawlph-validation` repository, NOT in the main `crawlph` repo
- Creating test issues in main repo pollutes the project backlog
- crawlph-validation is the dedicated sandbox for testing workflow scenarios

## Project Structure

```
crawlph/
├── skills/crawlph/           # Skill definition
│   └── SKILL.md             # Main workflow logic
├── openspec/
│   └── changes/
│       └── crawlph-skill/   # Change artifacts
│           ├── design.md
│           ├── specs/
│           └── tasks.md
├── AGENT-TEST-GUIDE.md      # ⭐ Testing guide for all changes
└── AGENTS.md                # This file
```

## Environment Notes

- **Always use WSL for validation** - Windows paths and tools cause path resolution issues
- Test repository: `suraciii/crawlph-test` (private)
- Test agent: `crawlph-test`
- Full workflow takes ~20-30 minutes
- Always test before modifying SKILL.md

## Non-Obvious Discoveries

### GitHub CLI Quirks
- `gh issue create` in non-interactive mode **requires both** `--title` and `--body` flags
- Workflow labels must be pre-created: crawlph expects `stage:*` and `action:*` labels to exist

### Validation Testing
- Create 5 experiment scenarios: Happy Path, Chaos Path, Concurrency, Re-evaluation, State Persistence
- Each scenario tests specific failure modes and recovery mechanisms
- Ralph Loop checkpoint granularity is at Stage level - mid-stage crashes lose progress

### Agent Debugging
- Session logs: `~/.openclaw/agents/{agent-id}/sessions/{session-id}.jsonl` - real-time execution trace
- Use `tail -f` on session logs to monitor sub-agent progress during long-running stages

### Model Configuration Constraints
- crawlph-test agent only supports: `zai/glm-5` or `kimi-coding/k2p5` models
- Other providers (minimax-portal, etc.) will fail with "model not found" errors
- Edit `~/.openclaw/agents/crawlph-test/models.json` to match supported providers

### Workspace Configuration Trap
- Agent workspace MUST match the GitHub repository with Issues to process
- Mismatch causes "no git remotes found" or "error: No such remote 'origin'"
- Fix: Update `workspace` path in `~/.openclaw/openclaw.json` agents list

### Ralph Loop API Limitations
- Design stage hangs silently on 429 (rate limit) errors without retry or timeout
- No automatic fallback to previous stage on API failures
- Workaround: Kill agent and restart from last checkpoint

### Windows Path Handling in WSL
- sed on Windows paths requires forward slash escaping: `sed 's|\\\\|/|g'`
- Or use WSL path format directly: `/mnt/c/Users/...`
- Git commands work but path resolution differs between Windows and WSL contexts

### openclaw.json 编辑陷阱
- 手动编辑 JSON 文件时，单引号 `'` 会导致 JSON 解析错误
- 必须使用双引号 `"` 包裹所有字符串值
- 使用 jq 或 Node.js 修改 JSON 更安全：`jq '.agents.list[0].workspace = "/new/path"' ~/.openclaw/openclaw.json`
