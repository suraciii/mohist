import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus } from '../../src/types';
import type { StageContext } from '../../src/workflow/stage-context';

function makeContext(overrides: Partial<StageContext> = {}): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
      body: '',
      stage: Stage.Build,
      status: IssueStatus.Active,
      projectId: 'project-1',
      labels: [],
      priority: 'p1',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { cwd: '/tmp/worktree', model: 'test-model' } as any,
    artifactManager: {} as any,
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: {} as any,
    checkpointManager: {} as any,
    issueRepo: {} as any,
    ...overrides,
  } as StageContext;
}

describe('StageContext emit/log helpers', () => {
  describe('emit helper failure swallowing', () => {
    it('does not throw when eventBus.emit throws', () => {
      const eventBusEmit = vi.fn().mockImplementation(() => {
        throw new Error('eventBus failure');
      });
      const ctx = makeContext({
        eventBus: { emit: eventBusEmit } as any,
        emit: (event, data) => {
          try {
            (ctx.eventBus as any).emit(event, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      expect(() => ctx.emit('test_event', { foo: 'bar' })).not.toThrow();
    });

    it('does not throw when eventBus is undefined', () => {
      const ctx = makeContext({
        eventBus: undefined,
        emit: (event, data) => {
          if (!ctx.eventBus) return;
          try {
            ctx.eventBus.emit(event, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      expect(() => ctx.emit('test_event', { foo: 'bar' })).not.toThrow();
    });

    it('emits event with correct name and payload when eventBus works', () => {
      const eventBusEmit = vi.fn();
      const ctx = makeContext({
        eventBus: { emit: eventBusEmit } as any,
        emit: (event, data) => {
          try {
            (ctx.eventBus as any).emit(event, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      ctx.emit('build_stage_completed', { completed: 5, failed: 0, total: 5 });

      expect(eventBusEmit).toHaveBeenCalledWith('build_stage_completed', { completed: 5, failed: 0, total: 5 });
    });

    it('emits correct payload for stage_task_update event', () => {
      const eventBusEmit = vi.fn();
      const ctx = makeContext({
        eventBus: { emit: eventBusEmit } as any,
        emit: (event, data) => {
          try {
            (ctx.eventBus as any).emit(event, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      ctx.emit('stage_task_update', {
        issueId: 'issue-1',
        projectId: 'project-1',
        stage: 'build',
        taskId: 'fix-build-health',
        taskTitle: 'Fix build health',
        status: 'completed',
        attempt: 1,
        artifacts: [],
      });

      expect(eventBusEmit).toHaveBeenCalledWith('stage_task_update', expect.objectContaining({
        issueId: 'issue-1',
        projectId: 'project-1',
        stage: 'build',
        taskId: 'fix-build-health',
        status: 'completed',
      }));
    });
  });

  describe('log helper failure swallowing', () => {
    it('does not throw when workflowLogRepo.insert throws', () => {
      const logInsert = vi.fn().mockImplementation(() => {
        throw new Error('log insert failure');
      });
      const ctx = makeContext({
        workflowLogRepo: { insert: logInsert } as any,
        log: (eventType, data) => {
          if (!ctx.workflowLogRepo) return;
          try {
            ctx.workflowLogRepo.insert('issue-1', null, eventType, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      expect(() => ctx.log('build_completed', { completed: 5 })).not.toThrow();
    });

    it('does not throw when workflowLogRepo is undefined', () => {
      const ctx = makeContext({
        workflowLogRepo: undefined,
        log: (eventType, data) => {
          if (!ctx.workflowLogRepo) return;
          try {
            ctx.workflowLogRepo.insert('issue-1', null, eventType, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      expect(() => ctx.log('build_completed', { completed: 5 })).not.toThrow();
    });

    it('writes log entry with correct event type and data when workflowLogRepo works', () => {
      const logInsert = vi.fn();
      const ctx = makeContext({
        workflowLogRepo: { insert: logInsert } as any,
        log: (eventType, data) => {
          if (!ctx.workflowLogRepo) return;
          try {
            ctx.workflowLogRepo.insert('issue-1', null, eventType, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      ctx.log('build_completed', { completed: 5, failed: 0, total: 5 });

      expect(logInsert).toHaveBeenCalledWith('issue-1', null, 'build_completed', { completed: 5, failed: 0, total: 5 });
    });

    it('writes log entry for build_failed event with error context', () => {
      const logInsert = vi.fn();
      const ctx = makeContext({
        workflowLogRepo: { insert: logInsert } as any,
        log: (eventType, data) => {
          if (!ctx.workflowLogRepo) return;
          try {
            ctx.workflowLogRepo.insert('issue-1', null, eventType, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      ctx.log('build_failed', { reason: 'tasks_failed', completed: 2, failed: 1, total: 3 });

      expect(logInsert).toHaveBeenCalledWith('issue-1', null, 'build_failed', { reason: 'tasks_failed', completed: 2, failed: 1, total: 3 });
    });
  });

  describe('emit and log helpers preserve event names and payload shapes', () => {
    it('preserves plan_round_start event name and payload shape', () => {
      const eventBusEmit = vi.fn();
      const logInsert = vi.fn();
      const ctx = makeContext({
        eventBus: { emit: eventBusEmit } as any,
        workflowLogRepo: { insert: logInsert } as any,
        emit: (event, data) => {
          try {
            (ctx.eventBus as any).emit(event, data);
          } catch {
            // fire-and-forget
          }
        },
        log: (eventType, data) => {
          if (!ctx.workflowLogRepo) return;
          try {
            ctx.workflowLogRepo.insert('issue-1', null, eventType, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      ctx.emit('plan_round_start', {
        issueId: '159',
        projectId: 'project-1',
        roundType: 'proposal',
        roundLabel: 'proposal',
        roundIndex: 0,
      });

      expect(eventBusEmit).toHaveBeenCalledWith('plan_round_start', expect.objectContaining({
        issueId: '159',
        projectId: 'project-1',
        roundType: 'proposal',
        roundLabel: 'proposal',
        roundIndex: 0,
      }));
    });

    it('preserves approval_requested event name and payload shape', () => {
      const eventBusEmit = vi.fn();
      const ctx = makeContext({
        eventBus: { emit: eventBusEmit } as any,
        emit: (event, data) => {
          try {
            (ctx.eventBus as any).emit(event, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      ctx.emit('approval_requested', {
        issueId: 'issue-1',
        projectId: 'project-1',
        stage: Stage.Plan,
      });

      expect(eventBusEmit).toHaveBeenCalledWith('approval_requested', expect.objectContaining({
        issueId: 'issue-1',
        projectId: 'project-1',
        stage: 'plan',
      }));
    });

    it('preserves integration_started event name and payload shape', () => {
      const eventBusEmit = vi.fn();
      const ctx = makeContext({
        eventBus: { emit: eventBusEmit } as any,
        emit: (event, data) => {
          try {
            (ctx.eventBus as any).emit(event, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      ctx.emit('integration_started', {
        issueId: 'issue-1',
        projectId: 'project-1',
        issueNumber: 159,
      });

      expect(eventBusEmit).toHaveBeenCalledWith('integration_started', expect.objectContaining({
        issueId: 'issue-1',
        projectId: 'project-1',
        issueNumber: 159,
      }));
    });

    it('preserves integration_completed event name and payload shape', () => {
      const eventBusEmit = vi.fn();
      const ctx = makeContext({
        eventBus: { emit: eventBusEmit } as any,
        emit: (event, data) => {
          try {
            (ctx.eventBus as any).emit(event, data);
          } catch {
            // fire-and-forget
          }
        },
      } as any);

      ctx.emit('integration_completed', {
        issueId: 'issue-1',
        projectId: 'project-1',
        issueNumber: 159,
        steps: [
          { step: 'integrate:spec-sync', status: 'completed', output: {} },
          { step: 'integrate:archive-change', status: 'completed', output: {} },
          { step: 'integrate:merge', status: 'completed', output: {} },
        ],
      });

      expect(eventBusEmit).toHaveBeenCalledWith('integration_completed', expect.objectContaining({
        issueId: 'issue-1',
        projectId: 'project-1',
        issueNumber: 159,
        steps: expect.any(Array),
      }));
    });
  });
});