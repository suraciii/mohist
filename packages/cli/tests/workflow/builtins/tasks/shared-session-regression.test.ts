import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus } from '../../../../src/types';
import type { StageContext, AgentSessionRegistry } from '../../../../src/workflow/stage-context';
import { InMemoryAgentSessionRegistry } from '../../../../src/workflow/stage-context';
import type { AgentSessionTaskInput } from '../../../../src/workflow/builtins/tasks';
import { createAgentSessionTaskHandler } from '../../../../src/workflow/builtins/tasks';

vi.mock('../../../../src/agent-runtime', () => ({
  AgentSession: {
    create: vi.fn(),
  },
  createWorkflowSessionObservers: vi.fn().mockReturnValue([]),
}));

function makeContext(registry?: AgentSessionRegistry): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 231,
      title: 'Shared Session Issue',
      body: 'Test body',
      stage: Stage.Plan,
      status: IssueStatus.Active,
      projectId: 'project-1',
      labels: [],
      priority: 'p1',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { cwd: '/tmp/worktree' } as any,
    artifactManager: {} as any,
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: { emit: vi.fn() } as any,
    checkpointManager: {} as any,
    issueRepo: {} as any,
    workflowLogRepo: undefined,
    sessionStreamLogRepo: undefined,
    coderSessionRepo: undefined,
    stageExecutionRepo: undefined,
    checkSuiteRepo: undefined,
    stageStateService: undefined,
    workflowRunService: undefined,
    workflowApplicationService: undefined,
    workflowRun: undefined,
    requestedWork: undefined,
    requestedTask: undefined,
    signal: undefined,
    agentSessionRegistry: registry,
    emit: vi.fn(),
    log: vi.fn(),
  };
}

function makeTaskInput(overrides: Partial<AgentSessionTaskInput> = {}): AgentSessionTaskInput {
  return {
    taskId: 'proposal',
    title: 'Generate proposal',
    prompt: 'Write proposal',
    cwd: '/tmp/worktree',
    stage: 'plan',
    attempt: 1,
    ...overrides,
  };
}

function mockSession(acpSessionId: string) {
  const execute = vi.fn().mockResolvedValue({
    success: true,
    text: 'done',
    acpSessionId,
  });
  const close = vi.fn().mockResolvedValue(undefined);
  return { execute, close, acpSessionId };
}

describe('T-006: Shared-session runtime regressions', () => {

  describe('Plan artifact tasks share one acpSessionId while preserving separate task results', () => {
    it('multiple Plan artifact tasks with plan-artifacts ref share one session and report same acpSessionId', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const ctx = makeContext(registry);
      const session = mockSession('shared-session-1');

      const createSession = vi.fn().mockResolvedValue({
        execute: session.execute,
        close: session.close,
      });

      const handler = createAgentSessionTaskHandler({ createSession });

      const planTasks: Array<{ taskId: string; title: string }> = [
        { taskId: 'proposal', title: 'Generate proposal' },
        { taskId: 'specs', title: 'Write specs' },
        { taskId: 'design', title: 'Create design' },
        { taskId: 'tasks', title: 'Generate tasks' },
        { taskId: 'self-review', title: 'Self review' },
      ];

      const results = [];
      for (const task of planTasks) {
        const result = await handler(
          makeTaskInput({ taskId: task.taskId, title: task.title, agentSessionRef: 'plan-artifacts' }),
          ctx,
        );
        results.push(result);
      }

      expect(createSession).toHaveBeenCalledTimes(1);
      expect(session.execute).toHaveBeenCalledTimes(5);

      const sessionIds = results.map(r => (r.output as any).acpSessionId);
      expect(new Set(sessionIds).size).toBe(1);
      expect(sessionIds[0]).toBe('shared-session-1');

      for (let i = 0; i < planTasks.length; i++) {
        expect(results[i].taskId).toBe(planTasks[i].taskId);
        expect(results[i].title).toBe(planTasks[i].title);
        expect(results[i].status).toBe('completed');
      }

      expect(session.close).not.toHaveBeenCalled();
    });

    it('each Plan artifact task has independent duration and attempt count', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const ctx = makeContext(registry);
      const session = mockSession('shared-session-2');

      const handler = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: session.execute,
          close: session.close,
        }),
      });

      const r1 = await handler(
        makeTaskInput({ taskId: 'proposal', title: 'Generate proposal', attempt: 1, agentSessionRef: 'plan-artifacts' }),
        ctx,
      );
      const r2 = await handler(
        makeTaskInput({ taskId: 'specs', title: 'Write specs', attempt: 1, agentSessionRef: 'plan-artifacts' }),
        ctx,
      );

      expect(r1.taskId).toBe('proposal');
      expect(r2.taskId).toBe('specs');
      expect(r1.attempts).toBe(1);
      expect(r2.attempts).toBe(1);
      expect(typeof r1.duration).toBe('number');
      expect(typeof r2.duration).toBe('number');
    });
  });

  describe('Tasks without agentSessionRef create separate task-local sessions and close them per task', () => {
    it('omitted agentSessionRef creates a new session per task and closes each one', async () => {
      const ctx = makeContext();
      const session1 = mockSession('local-1');
      const session2 = mockSession('local-2');

      const createSession = vi.fn()
        .mockResolvedValueOnce({ execute: session1.execute, close: session1.close })
        .mockResolvedValueOnce({ execute: session2.execute, close: session2.close });

      const handler = createAgentSessionTaskHandler({ createSession });

      const r1 = await handler(makeTaskInput({ taskId: 'task-a', agentSessionRef: undefined }), ctx);
      const r2 = await handler(makeTaskInput({ taskId: 'task-b', agentSessionRef: undefined }), ctx);

      expect(createSession).toHaveBeenCalledTimes(2);
      expect(session1.close).toHaveBeenCalledTimes(1);
      expect(session2.close).toHaveBeenCalledTimes(1);

      expect((r1.output as any).acpSessionId).toBe('local-1');
      expect((r2.output as any).acpSessionId).toBe('local-2');
    });

    it('task-local session is closed even when execution throws', async () => {
      const ctx = makeContext();
      const session = mockSession('local-err');
      session.execute.mockRejectedValue(new Error('boom'));

      const handler = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({ execute: session.execute, close: session.close }),
      });

      const result = await handler(makeTaskInput({ taskId: 'failing-task' }), ctx);

      expect(result.status).toBe('failed');
      expect(session.close).toHaveBeenCalledTimes(1);
    });
  });

  describe('Two different agentSessionRef values produce two real sessions', () => {
    it('tasks with different refs use separate sessions within one stage attempt', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const ctx = makeContext(registry);
      const sessionA = mockSession('session-requirements');
      const sessionB = mockSession('session-implementation');

      const createSession = vi.fn()
        .mockResolvedValueOnce({ execute: sessionA.execute, close: sessionA.close })
        .mockResolvedValueOnce({ execute: sessionB.execute, close: sessionB.close });

      const handler = createAgentSessionTaskHandler({ createSession });

      const r1 = await handler(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'requirements' }),
        ctx,
      );
      const r2 = await handler(
        makeTaskInput({ taskId: 'specs', agentSessionRef: 'requirements' }),
        ctx,
      );
      const r3 = await handler(
        makeTaskInput({ taskId: 'tasks', agentSessionRef: 'implementation-plan' }),
        ctx,
      );
      const r4 = await handler(
        makeTaskInput({ taskId: 'self-review', agentSessionRef: 'implementation-plan' }),
        ctx,
      );

      expect(createSession).toHaveBeenCalledTimes(2);
      expect((r1.output as any).acpSessionId).toBe('session-requirements');
      expect((r2.output as any).acpSessionId).toBe('session-requirements');
      expect((r3.output as any).acpSessionId).toBe('session-implementation');
      expect((r4.output as any).acpSessionId).toBe('session-implementation');

      expect(sessionA.close).not.toHaveBeenCalled();
      expect(sessionB.close).not.toHaveBeenCalled();

      for (const r of [r1, r2, r3, r4]) {
        expect(r.status).toBe('completed');
      }
    });
  });

  describe('Restored or skipped intermediate tasks do not create session and do not change ref for later tasks', () => {
    it('skipped intermediate task (no agentSessionRef in input) does not affect later shared tasks', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const ctx = makeContext(registry);
      const sharedSession = mockSession('plan-shared');

      const handler = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: sharedSession.execute,
          close: sharedSession.close,
        }),
      });

      const skippedResult: import('../../../../src/workflow/stage-context').StageTaskResult = {
        taskId: 'specs',
        title: 'Write specs',
        status: 'skipped',
        artifacts: [],
        attempts: 1,
        duration: 0,
        reason: 'Restored from checkpoint',
      };

      const r1 = await handler(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'plan-artifacts' }),
        ctx,
      );

      const r3 = await handler(
        makeTaskInput({ taskId: 'design', agentSessionRef: 'plan-artifacts' }),
        ctx,
      );

      expect(r1.status).toBe('completed');
      expect(r3.status).toBe('completed');
      expect((r1.output as any).acpSessionId).toBe('plan-shared');
      expect((r3.output as any).acpSessionId).toBe('plan-shared');
    });

    it('restored artifact task dispatched as service-call does not touch agentSessionRegistry', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const ctx = makeContext(registry);
      const session = mockSession('after-restore');

      const createSession = vi.fn().mockResolvedValue({
        execute: session.execute,
        close: session.close,
      });

      const handler = createAgentSessionTaskHandler({ createSession });

      const result = await handler(
        makeTaskInput({ taskId: 'design', agentSessionRef: 'plan-artifacts' }),
        ctx,
      );

      expect(result.status).toBe('completed');
      expect((result.output as any).acpSessionId).toBe('after-restore');
      expect(createSession).toHaveBeenCalledTimes(1);
    });

    it('task executed after skipped task still gets the same shared session', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const ctx = makeContext(registry);
      const sharedSession = mockSession('plan-after-skip');

      const handler = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: sharedSession.execute,
          close: sharedSession.close,
        }),
      });

      const r1 = await handler(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'plan-artifacts' }),
        ctx,
      );

      const r3 = await handler(
        makeTaskInput({ taskId: 'tasks', agentSessionRef: 'plan-artifacts' }),
        ctx,
      );

      expect((r1.output as any).acpSessionId).toBe('plan-after-skip');
      expect((r3.output as any).acpSessionId).toBe('plan-after-skip');
      expect(sharedSession.execute).toHaveBeenCalledTimes(2);
    });
  });

  describe('Retry, rerun, or rewind creates a new real session for the same logical ref', () => {
    it('new stage attempt with fresh registry creates a new session for the same ref', async () => {
      const firstRegistry = new InMemoryAgentSessionRegistry();
      const ctx1 = makeContext(firstRegistry);
      const firstSession = mockSession('attempt-1-session');

      const handler1 = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: firstSession.execute,
          close: firstSession.close,
        }),
      });

      await handler1(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'plan-artifacts' }),
        ctx1,
      );

      const secondRegistry = new InMemoryAgentSessionRegistry();
      const ctx2 = makeContext(secondRegistry);
      const secondSession = mockSession('attempt-2-session');

      const handler2 = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: secondSession.execute,
          close: secondSession.close,
        }),
      });

      const r2 = await handler2(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'plan-artifacts' }),
        ctx2,
      );

      expect((r2.output as any).acpSessionId).toBe('attempt-2-session');
      expect(secondSession.execute).toHaveBeenCalledTimes(1);
    });

    it('closing first registry and using new registry isolates sessions', async () => {
      const firstRegistry = new InMemoryAgentSessionRegistry();
      const ctx1 = makeContext(firstRegistry);
      const firstSession = mockSession('first-attempt');

      const handler1 = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: firstSession.execute,
          close: firstSession.close,
        }),
      });

      await handler1(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'plan-artifacts' }),
        ctx1,
      );

      await firstRegistry.closeAll();
      expect(firstSession.close).toHaveBeenCalledTimes(1);

      const secondRegistry = new InMemoryAgentSessionRegistry();
      const ctx2 = makeContext(secondRegistry);
      const secondSession = mockSession('second-attempt');

      const handler2 = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: secondSession.execute,
          close: secondSession.close,
        }),
      });

      const r2 = await handler2(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'plan-artifacts' }),
        ctx2,
      );

      expect((r2.output as any).acpSessionId).toBe('second-attempt');
      expect(firstSession.execute).toHaveBeenCalledTimes(1);
      expect(secondSession.execute).toHaveBeenCalledTimes(1);
    });

    it('old session transcript is not appended to after registry is closed', async () => {
      const firstRegistry = new InMemoryAgentSessionRegistry();
      const ctx1 = makeContext(firstRegistry);
      const firstSession = mockSession('original-session');

      const handler1 = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: firstSession.execute,
          close: firstSession.close,
        }),
      });

      await handler1(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'plan-artifacts' }),
        ctx1,
      );
      await handler1(
        makeTaskInput({ taskId: 'specs', agentSessionRef: 'plan-artifacts' }),
        ctx1,
      );

      await firstRegistry.closeAll();

      expect(firstSession.execute).toHaveBeenCalledTimes(2);

      const secondRegistry = new InMemoryAgentSessionRegistry();
      const ctx2 = makeContext(secondRegistry);
      const secondSession = mockSession('retry-session');

      const handler2 = createAgentSessionTaskHandler({
        createSession: vi.fn().mockResolvedValue({
          execute: secondSession.execute,
          close: secondSession.close,
        }),
      });

      await handler2(
        makeTaskInput({ taskId: 'proposal', agentSessionRef: 'plan-artifacts' }),
        ctx2,
      );

      expect(firstSession.execute).toHaveBeenCalledTimes(2);
      expect(secondSession.execute).toHaveBeenCalledTimes(1);
    });
  });

  describe('InMemoryAgentSessionRegistry', () => {
    it('getOrCreate returns same session for same ref', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const session = mockSession('shared');
      const factory = vi.fn().mockResolvedValue({ execute: session.execute, close: session.close });

      const s1 = await registry.getOrCreate('plan-artifacts', factory);
      const s2 = await registry.getOrCreate('plan-artifacts', factory);

      expect(s1).toBe(s2);
      expect(factory).toHaveBeenCalledTimes(1);
    });

    it('getOrCreate returns different sessions for different refs', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const sessionA = mockSession('a');
      const sessionB = mockSession('b');
      const factoryA = vi.fn().mockResolvedValue({ execute: sessionA.execute, close: sessionA.close });
      const factoryB = vi.fn().mockResolvedValue({ execute: sessionB.execute, close: sessionB.close });

      const s1 = await registry.getOrCreate('ref-a', factoryA);
      const s2 = await registry.getOrCreate('ref-b', factoryB);

      expect(s1).not.toBe(s2);
    });

    it('closeAll is idempotent', async () => {
      const registry = new InMemoryAgentSessionRegistry();
      const session = mockSession('idempotent');
      await registry.getOrCreate('plan-artifacts', vi.fn().mockResolvedValue({ execute: session.execute, close: session.close }));

      await registry.closeAll();
      await registry.closeAll();

      expect(session.close).toHaveBeenCalledTimes(1);
    });
  });
});
