## Context

The current `AgentRunnerService` implementation tracks a single active agent via `activePromise: Promise<void> | null`. When `start()` is called while an agent is already running, it throws an error. However:

1. The `maxConcurrentAgents` configuration exists (default: 8, range: 1-16) and is displayed in server logs
2. The server was architected to support concurrent agents
3. The single-agent limitation was never lifted despite this infrastructure

**Current flow:**
```
POST /issues/1/start → activePromise = runAgent(issue1) → success
POST /issues/2/start → Error: "Another issue is already running"
```

**Goal flow:**
```
POST /issues/1/start → activeAgents.set(id1, runAgent(issue1)) → queued or started
POST /issues/2/start → activeAgents.set(id2, runAgent(issue2)) → queued or started
... up to maxConcurrentAgents
```

## Goals / Non-Goals

**Goals:**
- Enable concurrent agent execution up to `maxConcurrentAgents` limit
- Queue new agent requests when at capacity instead of rejecting
- Track individual agent status per issueId for pause/cancel/resume
- Maintain backward compatibility for pause/resume/approve workflow
- Keep the existing EventBus integration for SSE updates

**Non-Goals:**
- Session persistence across server restarts (separate change)
- Distributed agent execution across multiple servers
- Priority-based queue ordering (FIFO is sufficient)
- Automatic load balancing or preemption

## Decisions

### 1. Replace single promise with Map-based tracking

**Current:**
```typescript
private activePromise: Promise<void> | null = null;
private activeIssueId: string | null = null;
private activeIssueNumber: number | null = null;
```

**Proposed:**
```typescript
interface RunningAgent {
  issueId: string;
  issueNumber: number;
  promise: Promise<void>;
  projectId: string;
}

private activeAgents = new Map<string, RunningAgent>();
private agentQueue: QueuedAgent[] = [];
private maxConcurrentAgents: number;
```

**Rationale:** Map allows O(1) lookup by issueId for pause/cancel/resume operations. Queue handles overflow.

### 2. Queue model instead of reject on capacity

**Current:** `start()` throws if `activePromise !== null`

**Proposed:**
- If `activeAgents.size < maxConcurrentAgents`: start immediately
- If `activeAgents.size >= maxConcurrentAgents`: add to queue
- When an agent completes: start next from queue
- Queue is FIFO, no priority

**Alternative considered - backpressure rejection:**
- Return 503 Service Unavailable when at capacity
- Rejected: User experience is worse, poll-based retry needed

### 3. AgentRunnerService receives maxConcurrentAgents via constructor

**Proposed:**
```typescript
constructor(
  private readonly eventBus: EventBus,
  private readonly maxConcurrentAgents: number = 8,
) {}
```

**Alternative - read from ConfigService directly:**
- Rejected: Creates direct dependency on ConfigService, harder to test
- Current `AgentRunnerService` is created in `server/index.ts` which has access to `ConfigService`

### 4. Update API to handle queued responses

**Current:** `/issues/:number/start` returns immediately with success

**Proposed:** `/issues/:number/start` returns 202 Accepted with queue position:
```json
{
  "success": true,
  "data": {
    "issue": {...},
    "message": "Issue #1 queued, position: 2/8",
    "queuePosition": 2,
    "runningAgents": 6
  }
}
```

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| Resource contention: Multiple agents writing to DB simultaneously | SQLite WAL mode handles concurrent writes; worktrees are isolated per issue |
| Memory pressure: 8 concurrent LLM contexts | Default limit of 8 is conservative; user can reduce via config |
| Queue growth: Many issues queued, long wait times | Add monitoring (future); consider circuit breaker if needed |
| Race condition: Queue check vs agent completion | Use async queue processing with proper locking |
| EventBus flooding: 8x the events | Events are ephemeral; no persistence concern |

## Migration Plan

**Phase 1 - Core Changes:**
1. Add `RunningAgent` interface and `activeAgents` Map to `AgentRunnerService`
2. Implement queue data structure
3. Modify `start()` to check capacity and either start or queue
4. Add `processQueue()` called when agent completes

**Phase 2 - API Updates:**
1. Update `/issues/:number/start` to return queue position
2. Update error messages (already done in quick fix)

**Phase 3 - Queue Status API (optional):**
1. Add `/api/agent/queue` endpoint for debugging

**Rollback:** Single-file revert of `agent-runner-service.ts` restores single-agent behavior.

## Open Questions

1. **Queue persistence across server restart?** — Not in scope for this change, but design should not preclude it.
2. **Should we emit a `agent_queued` event?** — Useful for UI, but adds complexity. Defer.
3. **Timeout for queued requests?** — If queue waits too long, should we reject? Not implementing now, monitor.
4. **CLI output for queued start?** — Should `mo issue start 1` show queue position? Follow-up work.
