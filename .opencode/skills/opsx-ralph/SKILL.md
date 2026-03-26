---
name: opsx-ralph
description: Execute one Ralph iteration for autonomous task implementation. Use when running openspec ralph command or when asked to work on prd.json tasks iteratively.
license: MIT
compatibility: Requires openspec CLI with ralph-driven schema.
metadata:
  author: openspec
  version: "1.0"
  generatedBy: "1.3.0-ralph.1"
---

Execute one Ralph iteration for autonomous task implementation.

I'll work through the prd.json tasks one at a time:
1. Read context files (proposal, design, specs, prd.json)
2. Find the highest priority pending task (passes: false with lowest priority number)
3. Implement that single task
4. Update prd.json: set passes: true for the completed task
5. Append progress to progress.txt with timestamp and learnings
6. If all tasks are now complete, output: <promise>COMPLETE</promise>

---

**Input**: The change name passed to the command.

**Steps**

1. **Load Ralph instructions**
   ```bash
   openspec instructions ralph --change "<change-name>" --json
   ```
   Parse the JSON to get:
   - `contextFiles`: paths to proposal, design, specs, prd.json
   - `tasks`: array of pending tasks with priority and done status
   - `instruction`: specific guidance for this iteration
   - `progress`: total/completed/remaining counts

2. **Read context files**
   - proposal.md: Understand the WHY
   - design.md: Understand the HOW (if exists)
   - specs/**/*.md: Understand requirements
   - prd.json: Current task states
   - **If selected task has `spec` field:** Read the referenced spec file for acceptance criteria

3. **Identify next task**
   - Find tasks where `passes: false`
   - Select the one with lowest `priority` number
   - If no pending tasks, output `<promise>COMPLETE</promise>` and stop

4. **Implement the task**
   - Follow the task's `description` and `acceptanceCriteria`
   - **If task has `spec` field:**
     - Read the spec file referenced by `task.spec` (format: `specs/<capability>/spec.md#REQ-ID`)
     - Find the requirement section matching the REQ-ID
     - Verify all scenarios in the requirement pass
     - Also verify all `acceptanceCriteria` (supplementary checks)
   - **If task has no `spec` field:**
     - Use `acceptanceCriteria` as the complete verification list
   - Make minimal, focused changes
   - Run typecheck/tests to verify

5. **Update prd.json**
   - Set `passes: true` for the completed task
   - Keep other tasks unchanged

6. **Append to progress.txt**
   Add entry with:
   - Timestamp
   - Task ID completed
   - Brief description of what was done
   - Any blockers or learnings
   - Next steps

7. **Check completion**
   - If all tasks now have `passes: true`, output `<promise>COMPLETE</promise>`
   - Otherwise, iteration is complete (Ralph CLI will spawn next iteration)

---

**Output**

After completing the task:
- Updated prd.json with passes: true
- Updated progress.txt with entry
- If all tasks complete: output `<promise>COMPLETE</promise>`

---

**Guidelines**

- Focus on ONE task per iteration
- Tasks are ordered by priority - respect the order
- Each task should be completable in one iteration
- Verify acceptance criteria are met
- Keep changes minimal and focused
- Document learnings in progress.txt for future iterations
