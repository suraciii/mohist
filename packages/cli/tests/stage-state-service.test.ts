import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { Stage } from '../src/types';
import { StageStateService, normalizeCheckStatus, normalizeTaskStatus } from '../src/services/stage-state-service';

describe('StageStateService', () => {
  let db: DatabaseManager;
  let service: StageStateService;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });

    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;

    service = new StageStateService(db);
  });

  afterEach(() => {
    db.close();
  });

  describe('ensureStage', () => {
    it('should create a stage state row with pending status', () => {
      service.ensureStage(issueId, Stage.Plan);

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state).not.toBeNull();
      expect(state!.stage).toBe(Stage.Plan);
      expect(state!.status).toBe('pending');
      expect(state!.attempts).toBe(0);
    });

    it('should seed static tasks for plan stage', () => {
      service.ensureStage(issueId, Stage.Plan);

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.tasks.length).toBe(5);
      expect(state!.tasks[0].taskId).toBe('read-context');
      expect(state!.tasks[0].status).toBe('pending');
      expect(state!.tasks[0].source).toBe('static');
    });

    it('should seed static tasks for check stage', () => {
      service.ensureStage(issueId, Stage.Check);

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.tasks.length).toBe(3);
      expect(state!.tasks[0].taskId).toBe('build-test');
    });

    it('should seed static tasks for integrate stage', () => {
      service.ensureStage(issueId, Stage.Integrate);

      const state = service.getStageState(issueId, Stage.Integrate);
      expect(state!.tasks.length).toBe(2);
      expect(state!.tasks[0].taskId).toBe('merge-branch');
    });

    it('should not seed tasks for build stage (dynamic tasks)', () => {
      service.ensureStage(issueId, Stage.Build);

      const state = service.getStageState(issueId, Stage.Build);
      expect(state!.tasks.length).toBe(0);
    });

    it('should increment attempts on repeated calls', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.ensureStage(issueId, Stage.Plan);

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.attempts).toBe(1);
      expect(state!.status).toBe('running');
    });

    it('should not re-seed static tasks on repeated calls', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.ensureStage(issueId, Stage.Plan);

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.tasks.length).toBe(5);
    });
  });

  describe('upsertTask', () => {
    it('should insert a new task', () => {
      service.ensureStage(issueId, Stage.Build);
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'compile',
        title: 'Compile code',
        status: 'completed',
        source: 'dynamic',
        order: 1,
      });

      const state = service.getStageState(issueId, Stage.Build);
      expect(state!.tasks.length).toBe(1);
      expect(state!.tasks[0].taskId).toBe('compile');
      expect(state!.tasks[0].status).toBe('completed');
      expect(state!.tasks[0].source).toBe('dynamic');
    });

    it('should update existing task in place', () => {
      service.ensureStage(issueId, Stage.Plan);

      service.upsertTask(issueId, Stage.Plan, {
        taskId: 'read-context',
        title: 'Read context files',
        status: 'running',
      });

      const state1 = service.getStageState(issueId, Stage.Plan);
      expect(state1!.tasks.find(t => t.taskId === 'read-context')!.status).toBe('running');

      service.upsertTask(issueId, Stage.Plan, {
        taskId: 'read-context',
        title: 'Read context files',
        status: 'completed',
        attempts: 1,
        duration: 5000,
      });

      const state2 = service.getStageState(issueId, Stage.Plan);
      const task = state2!.tasks.find(t => t.taskId === 'read-context');
      expect(task!.status).toBe('completed');
      expect(task!.attempts).toBe(1);
      expect(task!.duration).toBe(5000);
    });

    it('should store artifacts and output', () => {
      service.ensureStage(issueId, Stage.Build);
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'compile',
        title: 'Compile',
        status: 'completed',
        artifacts: ['dist/main.js', 'dist/vendor.js'],
        output: { lines: 42 },
      });

      const state = service.getStageState(issueId, Stage.Build);
      const task = state!.tasks.find(t => t.taskId === 'compile');
      expect(task!.artifacts).toEqual(['dist/main.js', 'dist/vendor.js']);
      expect(task!.output).toEqual({ lines: 42 });
    });

    it('should create stage row automatically if missing', () => {
      service.upsertTask(issueId, Stage.Build, {
        taskId: 'compile',
        title: 'Compile',
        status: 'running',
      });

      const state = service.getStageState(issueId, Stage.Build);
      expect(state).not.toBeNull();
      expect(state!.tasks.length).toBe(1);
    });

    it('should support dynamic fix tasks', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertTask(issueId, Stage.Check, {
        taskId: 'fix-check-health',
        title: 'Fix check health issues',
        status: 'completed',
        source: 'dynamic',
        order: 100,
      });

      const state = service.getStageState(issueId, Stage.Check);
      const fixTask = state!.tasks.find(t => t.taskId === 'fix-check-health');
      expect(fixTask).toBeDefined();
      expect(fixTask!.source).toBe('dynamic');
    });
  });

  describe('upsertCheck', () => {
    it('should insert a new check', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
        message: 'All tests passed',
      });

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.checks.length).toBe(1);
      expect(state!.checks[0].checkName).toBe('build-test');
      expect(state!.checks[0].status).toBe('passed');
      expect(state!.checks[0].message).toBe('All tests passed');
    });

    it('should update existing check in place', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'failed',
        message: 'Test failed',
      });

      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
        message: 'All tests passed',
      });

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.checks.length).toBe(1);
      expect(state!.checks[0].status).toBe('passed');
      expect(state!.checks[0].runCount).toBe(2);
    });

    it('should accept explicit runCount', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
        runCount: 3,
      });

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.checks[0].runCount).toBe(3);
    });

    it('should store output', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'ai-review',
        status: 'passed',
        output: { verdict: 'PASS', reviewReport: 'LGTM' },
      });

      const state = service.getStageState(issueId, Stage.Check);
      expect(state!.checks[0].output).toEqual({ verdict: 'PASS', reviewReport: 'LGTM' });
    });
  });

  describe('setApproval', () => {
    it('should set approval state on a stage', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.setApproval(issueId, Stage.Plan, {
        status: 'awaiting',
        output: { result: 'PASS' },
        requestedAt: '2024-01-01T00:00:00Z',
      });

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.approval).not.toBeNull();
      expect(state!.approval!.status).toBe('awaiting');
      expect(state!.approval!.output).toEqual({ result: 'PASS' });
    });

    it('should update approval state', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.setApproval(issueId, Stage.Plan, {
        status: 'awaiting',
        requestedAt: '2024-01-01T00:00:00Z',
      });
      service.setApproval(issueId, Stage.Plan, {
        status: 'approved',
        respondedAt: '2024-01-01T01:00:00Z',
      });

      const state = service.getStageState(issueId, Stage.Plan);
      expect(state!.approval!.status).toBe('approved');
      expect(state!.approval!.respondedAt).toBe('2024-01-01T01:00:00Z');
    });
  });

  describe('getIssueStageState', () => {
    it('should return empty array when no stages exist', () => {
      const states = service.getIssueStageState(issueId);
      expect(states).toEqual([]);
    });

    it('should return all stages for an issue', () => {
      service.ensureStage(issueId, Stage.Plan);
      service.ensureStage(issueId, Stage.Check);

      const states = service.getIssueStageState(issueId);
      expect(states.length).toBe(2);

      const stages = states.map(s => s.stage);
      expect(stages).toContain(Stage.Plan);
      expect(stages).toContain(Stage.Check);
    });

    it('should include tasks and checks for each stage', () => {
      service.ensureStage(issueId, Stage.Check);
      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
      });

      const states = service.getIssueStageState(issueId);
      const checkState = states.find(s => s.stage === Stage.Check);
      expect(checkState).toBeDefined();
      expect(checkState!.tasks.length).toBe(3);
      expect(checkState!.checks.length).toBe(1);
    });
  });

  describe('getStageState', () => {
    it('should return null for non-existent stage', () => {
      const state = service.getStageState(issueId, Stage.Plan);
      expect(state).toBeNull();
    });
  });

  describe('normalizeCheckStatus', () => {
    it('should normalize pass to passed', () => {
      expect(normalizeCheckStatus('pass')).toBe('passed');
    });

    it('should normalize passed to passed', () => {
      expect(normalizeCheckStatus('passed')).toBe('passed');
    });

    it('should normalize fail to failed', () => {
      expect(normalizeCheckStatus('fail')).toBe('failed');
    });

    it('should normalize error to error', () => {
      expect(normalizeCheckStatus('error')).toBe('error');
    });

    it('should normalize unknown to pending', () => {
      expect(normalizeCheckStatus('unknown')).toBe('pending');
    });

    it('should keep running as running', () => {
      expect(normalizeCheckStatus('running')).toBe('running');
    });
  });

  describe('normalizeTaskStatus', () => {
    it('should normalize completed to completed', () => {
      expect(normalizeTaskStatus('completed')).toBe('completed');
    });

    it('should normalize failed to failed', () => {
      expect(normalizeTaskStatus('failed')).toBe('failed');
    });

    it('should normalize unknown to pending', () => {
      expect(normalizeTaskStatus('unknown')).toBe('pending');
    });

    it('should normalize skipped to skipped', () => {
      expect(normalizeTaskStatus('skipped')).toBe('skipped');
    });
  });

  describe('stage retry scenario', () => {
    it('should update current task state across retries', () => {
      service.ensureStage(issueId, Stage.Build);

      service.upsertTask(issueId, Stage.Build, {
        taskId: 'compile',
        title: 'Compile',
        status: 'failed',
        attempts: 1,
      });

      const state1 = service.getStageState(issueId, Stage.Build);
      expect(state1!.tasks[0].status).toBe('failed');
      expect(state1!.tasks[0].attempts).toBe(1);

      service.ensureStage(issueId, Stage.Build);

      service.upsertTask(issueId, Stage.Build, {
        taskId: 'compile',
        title: 'Compile',
        status: 'completed',
        attempts: 2,
      });

      const state2 = service.getStageState(issueId, Stage.Build);
      expect(state2!.tasks[0].status).toBe('completed');
      expect(state2!.tasks[0].attempts).toBe(2);
      expect(state2!.attempts).toBe(1);
    });

    it('should update check across fix-recheck cycles', () => {
      service.ensureStage(issueId, Stage.Check);

      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'failed',
        message: 'Test failed',
      });

      const state1 = service.getStageState(issueId, Stage.Check);
      expect(state1!.checks[0].status).toBe('failed');
      expect(state1!.checks[0].runCount).toBe(1);

      service.upsertCheck(issueId, Stage.Check, {
        checkName: 'build-test',
        status: 'passed',
        message: 'All tests passed',
      });

      const state2 = service.getStageState(issueId, Stage.Check);
      expect(state2!.checks[0].status).toBe('passed');
      expect(state2!.checks[0].runCount).toBe(2);
    });
  });
});
