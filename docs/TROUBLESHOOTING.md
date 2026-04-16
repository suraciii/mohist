# Troubleshooting Guide

Common issues and solutions for Mohist's OpenSpec workflow.

## Plan Stage Issues

### Issue: Plan stage fails with "Self-review did not pass"

**Symptoms**: Plan stage completes but marks as failed after 3 iterations.

**Cause**: Agent's self-review found issues that couldn't be resolved.

**Solution**:
1. Check artifacts in `openspec/changes/{change}/`
2. Manually fix issues in proposal.md, design.md, or specs/
3. Run `mo issue resume <id> --skip-to-review` to proceed

### Issue: prd.json not generated

**Symptoms**: Plan stage seems to complete but no prd.json in change directory.

**Cause**: Self-review may have failed silently.

**Solution**:
1. Verify specs are complete in `specs/` directory
2. Check each spec has proper acceptance criteria
3. Ensure all REQ-XXX references are valid
4. Manually create prd.json if needed (see examples/)

## Build Stage Issues

### Issue: Task fails with "AC not satisfied"

**Symptoms**: Task executes but fails acceptance criteria check.

**Solution**:
1. Agent retries automatically (up to 2 times)
2. Each retry includes failure context
3. If persistent failure, check:
   - Acceptance criteria in prd.json
   - Spec requirements in specs/{capability}/spec.md
4. Manually verify implementation
5. If criteria are wrong, update them and resume

### Issue: Build hangs on a task

**Symptoms**: Task appears stuck, no progress for long time.

**Cause**: Likely timeout or agent waiting for input.

**Solution**:
1. Check agent logs in `.mohist/logs/`
2. If timeout, task may need to be split
3. User intervention: `mo issue pause <id>`
4. Investigate and resolve
5. Resume with `mo issue resume <id>`

### Issue: Wrong task executing

**Symptoms**: Build seems to skip or repeat tasks incorrectly.

**Cause**: task-status.json may be out of sync.

**Solution**:
1. Check `task-status.json` in change directory
2. Verify `current_task_index` matches expected task
3. Manually edit task-status.json if needed
4. Ensure previous task artifacts are correct

## Session Memory Issues

### Issue: Learnings not being loaded

**Symptoms**: Agent doesn't seem to remember previous task failures.

**Solution**:
1. Check `session-memories/` directory exists
2. Verify JSON files are valid
3. Ensure files are named `{task-id}.json`
4. Check file permissions

### Issue: Too many session memory files

**Symptoms**: Context becomes too long, performance issues.

**Solution**:
1. This is expected - all learnings are preserved
2. Consider archiving completed changes
3. Future version may support filtering

## Artifact Issues

### Issue: Change directory naming conflict

**Symptoms**: Error "Change already exists" or version confusion.

**Solution**:
- New proposals auto-create `-v2`, `-v3` etc.
- Use `--force` to overwrite existing
- Archive old versions: move to `openspec/changes/archive/`

### Issue: Specs not visible in PR

**Symptoms**: Reviewers can't see specs during code review.

**Solution**:
1. Verify `openspec/` is not in .gitignore
2. Ensure specs were committed with code
3. Check `.mohist/config.yaml` has `git_track: true`

## Server Issues

### Issue: Server won't start

**Symptoms**: `mo server start` fails.

**Solution**:
1. Check port not in use: `lsof -i :3456`
2. Check database permissions: `~/.mohist/`
3. View logs: `mo server logs`
4. Try restart: `mo server stop && mo server start`

### Issue: Agent not spawning

**Symptoms**: Build stage shows "spawning agent" but nothing happens.

**Solution**:
1. Verify opencode is installed: `opencode --version`
2. Check `opencode agent --local` works manually
3. Verify network/proxy settings
4. Check server logs for errors

## Configuration Issues

### Issue: OpenSpec workflow not detected

**Symptoms**: Issue goes to traditional workflow instead of Ralph loop.

**Cause**: prd.json not in expected location.

**Solution**:
1. Verify `openspec/changes/{change}/prd.json` exists
2. Check workflow-loader detects file
3. Ensure change name matches expected format

### Issue: Changes not archived

**Symptoms**: Completed changes stay in `changes/` instead of `archive/`.

**Solution**:
1. Verify check stage completed successfully
2. Manual archive: `mv changes/{name} archive/`
3. Check disk space and permissions

## Recovery Commands

```bash
# Resume from build failure
mo issue resume <id>

# Skip plan after manual fixes
mo issue resume <id> --skip-to-review

# Force restart plan
mo propose <id> --force

# View issue status
mo issue show <id>

# View change artifacts
ls -la openspec/changes/<change>/
```

## Getting Help

1. Check logs: `mo server logs`
2. Verify OpenSpec artifacts: `openspec/changes/`
3. Run typecheck: `npm run typecheck`
4. Run tests: `npm test`
5. Open an issue at https://github.com/owner/mohist/issues