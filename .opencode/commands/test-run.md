---
description: Execute an agentic test plan in a container
---

Execute an agentic test plan using **general subagent tasks** for each major step. Build the container, start it in background, then run through TESTPLAN.md phases via `podman exec`.

**Input**: The argument after the command is the feature name (kebab-case), matching a directory under `test/agentic/verify-<feature>/`.

---

## Critical Constraints

### Container-Only Execution

**All test operations MUST run inside the container.** The host machine must NOT be modified in any way:

- NO creating, editing, or deleting files on the host
- NO running application commands directly on the host
- NO modifying host state (databases, config files, environment variables)
- The only host-side operations allowed are: `podman build`, `podman run`, `podman exec`, `podman stop`, `podman rm` — these manage the container lifecycle only
- All test commands (`mo`, `curl`, scripts, etc.) MUST be executed via `podman exec`

Violating this constraint will pollute the host environment and invalidate test results.

### Report-Only, No Fixing

**Subagents MUST NOT attempt to fix any problems they encounter.** The role of test-run is strictly to observe and report:

- NO modifying source code, config, scripts, or any files to "make tests pass"
- NO reconfiguring services, restarting with different flags, or applying workarounds
- NO retrying with altered parameters to force a pass
- If a step fails, record the failure with full details (actual output, expected output, error messages) and move on

The sole purpose of test-run is to surface the true state of the system. Fixing issues is a separate concern handled by the user or a dedicated implementation task.

---

## Execution Model

Each major step is delegated to a **general subagent** via the `Task` tool (`subagent_type: "general"`). The orchestrator:

1. Prepares context (feature name, test plan content, container ID)
2. Passes all necessary information in the Task prompt (including the container-only constraint)
3. Collects results from each subagent
4. Manages container lifecycle (build → start → phases → stop)

Subagents are responsible for executing commands **exclusively inside the container** and returning structured pass/fail results. Subagents must **never attempt to fix or work around failures** — only observe and report.

---

## Steps

### Step 1: Determine feature name

If `{{input}}` is empty:
- List available test plans: `ls test/agentic/verify-*/`
- Ask the user which one to run

Otherwise use `{{input}}` as the feature name.

### Step 2: Verify test plan & read content

Use the Read tool to check `test/agentic/verify-<feature>/TESTPLAN.md` exists and read its full content.

Extract:
- Number of phases
- Which phases need `@scripts/` helpers
- Expected environment (server port, working directory, data paths)
- The exact commands and expected results for each phase

If the file doesn't exist, suggest running `/test-create <feature>` first and stop.

### Step 3: Build container image (subagent task)

Launch a general subagent to build the container image.

**Task prompt:**
```
Build the mohist-test container image. Run this command and return the result (success or error with full output):

podman build -t mohist-test \
  --build-arg USER_ID=$(id -u) \
  --build-arg GROUP_ID=$(id -g) \
  -f test/agentic/shared/Containerfile \
  .

Return your result in this format:
- BUILD: SUCCESS or FAILED
- If FAILED, include the full error output
```

### Step 4: Start container & wait for server (subagent task)

Launch a general subagent to start the container and verify the server is ready.

**Task prompt:**
```
Start the mohist-test container and wait for the server to be ready.

1. Start the container:
   CONTAINER_ID=$(podman run -d \
     --user $(id -u):$(id -g) \
     -p 3456:3456 \
     -v "$PWD/test/agentic/verify-<feature>/scripts:/app/scripts:ro,Z" \
     -v "$PWD/test/agentic/verify-<feature>/TESTPLAN.md:/app/TESTPLAN.md:ro,Z" \
     -w /app \
     mohist-test \
     sleep infinity)

2. Wait for mo-server (up to 15s):
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

Note: entrypoint.sh starts mo-server in background. The `sleep infinity` keeps the container alive.

Return your result in this format:
- CONTAINER_ID: <the container id>
- SERVER: READY or FAILED
- If FAILED, include the full error output
```

**Important:** Save the returned `CONTAINER_ID` for subsequent steps.

### Step 5: Execute each phase (one subagent task per phase)

For each phase in TESTPLAN.md, launch a **separate general subagent**. Include the phase details, container ID, and any helper script info in the prompt.

**Task prompt template:**
```
Execute Phase <N> of the agentic test plan inside a podman container.

CRITICAL: ALL commands must run inside the container via `podman exec`. Do NOT run any commands directly on the host. Do NOT create, modify, or delete any files on the host. Do NOT attempt to fix or work around any failures — only observe and report.

Container ID: <CONTAINER_ID>

Phase description from TESTPLAN.md:
<copy the full phase content including steps, commands, and expected results>

Run each command inside the container using:
podman exec "<CONTAINER_ID>" bash -c '<command>'

For each step:
1. Run the command
2. Compare actual output against expected output
3. Record PASS or FAIL

If the phase uses helper scripts (e.g., bash /app/scripts/restart-server.sh), run them the same way via podman exec.

Return your result in this format:
- Phase <N>: <name>
- Step 1: PASS/FAIL — <brief note if failed>
- Step 2: PASS/FAIL — <brief note if failed>
- ...
- OVERALL: PASS or FAIL
- If any step FAILED, include actual vs expected output for diagnosis
```

**Execution strategy:**
- Run phases **sequentially** (each phase may depend on state from previous phases)
- If a phase fails, record it and continue to the next phase
- Collect all results

### Step 6: Stop container (subagent task)

Launch a general subagent to clean up the container.

**Task prompt:**
```
Stop and remove the podman container:

CONTAINER_ID=<CONTAINER_ID>
podman stop "$CONTAINER_ID" > /dev/null 2>&1
podman rm "$CONTAINER_ID" > /dev/null 2>&1

Return: CLEANUP: SUCCESS or FAILED (with error details)
```

### Step 7: Collect & report results

Aggregate all phase results from the subagents and produce the final report.

---

## What to Do When a Phase Fails

- The subagent will include actual vs expected output for diagnosis
- Continue to the next phase (launch next subagent)
- At the end, summarize all failures

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
