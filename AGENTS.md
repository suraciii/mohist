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

## Notes

- Test repository: `suraciii/crawlph-test` (private)
- Test agent: `crawlph-test`
- Full workflow takes ~20-30 minutes
- Always test before modifying SKILL.md
