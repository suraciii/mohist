## ADDED Requirements

### Requirement: Robust Artifact JSON Parsing

#### Scenario: Handle Imperfect LLM JSON Output
- **GIVEN** Planner Agent expects valid JSON artifact output from LLM
- **WHEN** LLM returns malformed or imperfect JSON
- **THEN** the system should gracefully handle with multiple parsing strategies

**Acceptance Criteria:**
1. Implement `parseArtifactsWithFallback(text: string)` method in PlannerAgent
2. **Strategy 1: Direct JSON.parse()**
   - Try parsing text directly as JSON
   - Success rate: ~60% for well-formatted output
3. **Strategy 2: Markdown Code Block Extraction**
   - Extract content from ```json or ``` blocks
   - Handle nested code blocks
   - Success rate: +20% for markdown-wrapped output
4. **Strategy 3: Relaxed Parsing**
   - Remove JavaScript-style comments (// and /* */)
   - Fix trailing commas before } and ]
   - Normalize quotes
   - Success rate: +10% for near-valid JSON
5. **Strategy 4: Regex Field Extraction (Last Resort)**
   - Extract key fields using regex patterns
   - Construct minimal valid object
   - Success rate: +5% for severely malformed output
6. **Failure Handling:**
   - Log all parsing attempts and failures
   - Return null if all strategies fail
   - Trigger retry with more explicit prompt
   - Max 3 retries before giving up

**Implementation:**
```typescript
private async parseArtifactsWithFallback(text: string): Promise<Artifacts | null> {
  const strategies = [
    { name: 'direct', parser: () => JSON.parse(text) },
    { name: 'codeBlock', parser: () => this.parseFromCodeBlock(text) },
    { name: 'relaxed', parser: () => this.parseRelaxed(text) },
    { name: 'regex', parser: () => this.parseWithRegex(text) },
  ];
  
  for (const strategy of strategies) {
    try {
      const result = strategy.parser();
      if (this.validateArtifacts(result)) {
        console.log(`[PlannerAgent] Parsed artifacts using ${strategy.name} strategy`);
        return result;
      }
    } catch (error) {
      console.warn(`[PlannerAgent] ${strategy.name} parsing failed:`, error);
    }
  }
  
  console.error('[PlannerAgent] All parsing strategies failed');
  return null;
}

private validateArtifacts(artifacts: unknown): artifacts is Artifacts {
  return (
    typeof artifacts === 'object' &&
    artifacts !== null &&
    'proposal' in artifacts &&
    'design' in artifacts &&
    'specs' in artifacts &&
    Array.isArray((artifacts as Artifacts).specs)
  );
}
```

---

### Requirement: Task Status Persistence

#### Scenario: Reliable Task State Management
- **GIVEN`Build` phase executes tasks from prd.json
- **WHEN** tasks complete, fail, or retry
- **THEN** their status should be persisted reliably

**Acceptance Criteria:**
1. Task status stored in prd.json after each state change
2. Status values: `pending`, `in_progress`, `completed`, `failed`, `skipped`
3. Track attempt count for each task
4. Store error message for failed tasks
5. Support resuming from last failed task

**Status Transitions:**
```
pending → in_progress (when task starts)
in_progress → completed (on success)
in_progress → failed (on failure)
failed → in_progress (on retry)
failed → skipped (user choice)
```

**Data Structure:**
```typescript
interface TaskStatus {
  id: string;
  status: 'pending' | 'in_progress' | 'completed' | 'failed' | 'skipped';
  attempts: number;
  error?: string;
  startedAt?: string;
  completedAt?: string;
}

interface PrdJson {
  tasks: Task[];
  taskStatus?: TaskStatus[];  // Runtime status
}
```

**Storage Strategy:**
- Option A: Store in prd.json (current approach)
- Option B: Store in separate task-status.json
- Option C: Store in database

**Decision:** Keep Option A (prd.json) for simplicity, but enhance error handling

---

### Requirement: Approval State Atomicity

#### Scenario: Prevent Race Conditions in Approval
- **GIVEN** approval state transitions (awaiting → approved/rejected)
- **WHEN** multiple operations may modify state concurrently
- **THEN** transitions should be atomic and consistent

**Acceptance Criteria:**
1. `setApprovalState` updates both approval_state and updated_at in single transaction
2. State transitions are validated:
   - `undefined` → `awaiting` ✓
   - `awaiting` → `approved` ✓
   - `awaiting` → `rejected` ✓
   - `approved` → `awaiting` ✗ (invalid)
   - `rejected` → `awaiting` ✓ (can retry)
3. Concurrent modifications detected and handled
4. State changes logged for debugging

**State Machine:**
```
┌─────────┐    setApprovalState    ┌──────────┐
│  null   │───────────────────────▶│ awaiting │
└─────────┘                        └────┬─────┘
                                        │
              ┌─────────────────────────┼─────────────────────────┐
              │                         │                         │
              ▼                         ▼                         ▼
       ┌────────────┐          ┌────────────┐          ┌────────────┐
       │  approved  │          │  rejected  │          │  awaiting  │
       │  (final)   │          │  (retry)   │          │  (error)   │
       └────────────┘          └──────┬─────┘          └────────────┘
                                      │
                                      │ setApprovalState
                                      ▼
                                ┌──────────┐
                                │ awaiting │
                                │ (retry)  │
                                └──────────┘
```

**Implementation:**
```typescript
setApprovalState(issueId: string, approvalState: ApprovalState): Issue | null {
  const now = new Date().toISOString();
  
  // Validate transition
  const current = this.findById(issueId);
  if (current?.approvalState?.status === 'approved') {
    throw new Error('Cannot modify approved state');
  }
  
  this.db.run(
    'UPDATE issues SET approval_state = ?, updated_at = ? WHERE id = ?',
    [JSON.stringify(approvalState), now, issueId]
  );
  
  console.log(`[IssueRepo] Approval state for issue ${issueId}: ${approvalState.status}`);
  
  return this.findById(issueId);
}
```

---

### Requirement: Session Persistence on Pause

#### Scenario: Reliable Session State on Pause
- **GIVEN`AgentRunnerService.pause()` is called
- **WHEN`system needs to resume later
- **THEN`session state should be fully recoverable

**Acceptance Criteria:**
1. `SessionManager.pause()` persists all messages and context
2. `pausedSessions` Map stores session by issue number
3. Session can be resumed even after process restart (future enhancement)
4. Clear error if trying to resume non-existent session

**Current Implementation:**
```typescript
// AgentRunnerService
private pausedSessions = new Map<number, Session>();

pause(issueNumber: number, session: Session): void {
  sessionManager.pause(session.id);
  this.pausedSessions.set(issueNumber, session);
}

resume(issueNumber: number): Session {
  const session = this.pausedSessions.get(issueNumber);
  if (!session) {
    throw new Error(`No paused session for issue ${issueNumber}`);
  }
  this.pausedSessions.delete(issueNumber);
  sessionManager.resume(session.id);
  return session;
}
```

**Enhancement Considerations:**
- Persist paused sessions to disk for crash recovery
- Add expiration for old paused sessions
- Handle concurrent pause/resume attempts

---

### Requirement: Error Recovery and Cleanup

#### Scenario: Handle Failures Gracefully
- **GIVEN`workflow execution may fail at any point
- **WHEN`failure occurs
- **THEN`system should recover and cleanup appropriately

**Acceptance Criteria:**
1. Agent execution failure:
   - Update issue status to Blocked
   - Close session properly
   - Emit error event
   - Log detailed error information
2. Pause/Resume failure:
   - Detect invalid state transitions
   - Provide clear error messages
   - Allow manual recovery
3. Resource cleanup:
   - Close database connections on exit
   - Kill spawned processes on timeout
   - Clean up temporary files

**Error Categories:**
| Category | Example | Recovery |
|----------|---------|----------|
| Execution | spawn_coder fails | Retry or block |
| Timeout | Task exceeds limit | Abort or extend |
| State | Invalid approval transition | Log and reject |
| Resource | Out of memory | Scale or fail |
| External | Git command fails | Retry with backoff |

**Cleanup Checklist:**
- [ ] Process cleanup on timeout
- [ ] Session cleanup on error
- [ ] Event listener cleanup
- [ ] Temporary file cleanup
- [ ] Database connection cleanup
