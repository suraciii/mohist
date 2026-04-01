---
description: Execute an agentic test plan in a container
---

Execute an agentic test plan. Build the container, start it in background, then run through TESTPLAN.md phases via `podman exec`.

**Input**: The argument after the command is the feature name (kebab-case), matching a directory under `test/agentic/verify-<feature>/`.

---

## Steps

1. **Determine feature name**

   If `{{input}}` is empty:
   - List available test plans: `ls test/agentic/verify-*/`
   - Ask the user which one to run

   Otherwise use `{{input}}` as the feature name.

2. **Verify test plan exists**

   Check that `test/agentic/verify-<feature>/TESTPLAN.md` exists.
   If not, suggest running `/test-create <feature>` first.

3. **Read TESTPLAN.md**

   Read the full test plan to understand:
   - Number of phases
   - Which phases need `@scripts/` helpers
   - Expected environment (server port, working directory, data paths)

4. **Build the shared container image**

   ```bash
   podman build -t mohist-test \
     --build-arg USER_ID=$(id -u) \
     --build-arg GROUP_ID=$(id -g) \
     -f test/agentic/shared/Containerfile \
     .
   ```

5. **Start the container in background**

   ```bash
   CONTAINER_ID=$(podman run -d \
     --user $(id -u):$(id -g) \
     -p 3456:3456 \
     -v "$PWD/test/agentic/verify-<feature>/scripts:/app/scripts:ro,Z" \
     -v "$PWD/test/agentic/verify-<feature>/TESTPLAN.md:/app/TESTPLAN.md:ro,Z" \
     -w /app \
     mohist-test \
     sleep infinity)
   ```

   Wait for mo-server to be ready inside the container:

   ```bash
   for i in $(seq 1 30); do
     if podman exec "$CONTAINER_ID" curl -sf http://localhost:3456/api/health > /dev/null 2>&1; then
       break
     fi
     if [ $i -eq 30 ]; then
       echo "Server failed to start within 15s"
       podman stop "$CONTAINER_ID" && podman rm "$CONTAINER_ID"
       exit 1
     fi
     sleep 0.5
   done
   ```

   > Note: entrypoint.sh starts mo-server in background. The `sleep infinity` keeps the container alive.

6. **Execute each phase via `podman exec`**

   Work through TESTPLAN.md phase by phase. Run each command inside the container:

   ```bash
   podman exec "$CONTAINER_ID" bash -c '<command>'
   ```

   Examples:

   ```bash
   podman exec "$CONTAINER_ID" bash -c 'mo issue create "Add hello function" --body "test"'
   podman exec "$CONTAINER_ID" bash -c 'curl -sf http://localhost:3456/api/health'
   podman exec "$CONTAINER_ID" bash -c 'bash /app/scripts/restart-server.sh'
   ```

   For each phase:
   - Execute the steps described in TESTPLAN.md
   - Check output matches expected results
   - Record pass/fail

   After each phase, report: pass or fail (with what went wrong).

7. **Stop the container**

   ```bash
   podman stop "$CONTAINER_ID" > /dev/null 2>&1
   podman rm "$CONTAINER_ID" > /dev/null 2>&1
   ```

8. **Collect results**

   After all phases:
   - List each phase with pass/fail status
   - Total pass count, total fail count
   - Overall verdict: ALL PASSED or N TESTS FAILED

---

## What to Do When a Phase Fails

- Record the failure clearly (which phase, which step, actual vs expected)
- Continue to the next phase if possible
- At the end, summarize all failures with enough detail to diagnose

---

## Output

```
Phase 1: Build Verification        ... PASS
Phase 2: Server Health             ... PASS
Phase 3: Project Management        ... PASS
Phase 4: Issue CRUD                ... PASS
Phase 5: Data Persistence          ... PASS
Phase 6: Error Handling            ... PASS
Phase 7: API Response Structure    ... PASS

Result: 7/7 PASSED
```
