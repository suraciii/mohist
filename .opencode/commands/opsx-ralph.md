---
description: Execute one Ralph iteration for autonomous task implementation
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

**Input**: The change name passed after the command.

**Steps**

1. **Load Ralph instructions**
   ```bash
   openspec instructions ralph --change "<change-name>" --json
   ```

2. **Read context files**
   - proposal.md: Understand the WHY
   - design.md: Understand the HOW
   - specs/**/*.md: Understand requirements
   - prd.json: Current task states
   - **If selected task has `spec` field:** Read the referenced spec file for acceptance criteria

3. **Identify next task**
   - Find tasks where `passes: false`
   - Select the one with lowest `priority` number
   - If no pending tasks, output `<promise>COMPLETE</promise>` and stop

4. **Implement the task**
   - Follow the task's description and acceptanceCriteria
   - **If task has `spec` field:**
     - Read the spec file referenced by `task.spec` (format: `specs/<capability>/spec.md#REQ-ID`)
     - Find the requirement section matching the REQ-ID
     - Verify all scenarios in the requirement pass
     - Also verify all `acceptanceCriteria` (supplementary checks)
   - **If task has no `spec` field:**
     - Use `acceptanceCriteria` as the complete verification list
   - Make minimal, focused changes

5. **Update prd.json**
   - Set `passes: true` for the completed task

6. **Append to progress.txt**
   Add entry with timestamp, task ID, and learnings

7. **Check completion**
   - If all tasks complete: output `<promise>COMPLETE</promise>`
