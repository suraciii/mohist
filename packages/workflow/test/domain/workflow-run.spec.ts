import { describe, expect, it } from 'vitest';
import { WorkflowRun } from '../../src/domain';

const singleStage = () => [{
  stage: 'build',
  tasks: [{ id: 'compile', title: 'Compile', uses: 'spec/task' }],
  checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
}];

const twoStages = () => [
  {
    stage: 'plan',
    tasks: [{ id: 'draft', title: 'Draft', uses: 'spec/task' }],
    checks: [{ name: 'plan-ok', title: 'Plan OK', uses: 'spec/check' }],
  },
  {
    stage: 'build',
    tasks: [{ id: 'compile', title: 'Compile', uses: 'spec/task' }],
    checks: [{ name: 'build-ok', title: 'Build OK', uses: 'spec/check' }],
  },
];

const approvalStage = () => [{
  stage: 'plan',
  tasks: [{ id: 'draft', title: 'Draft', uses: 'spec/task' }],
  checks: [{ name: 'plan-ok', title: 'Plan OK', uses: 'spec/check' }],
  requiresApproval: true,
}];

describe('WorkflowRun', () => {
  describe('start', () => {
    it('transitions from pending to running', () => {
      const run = new WorkflowRun('r1', singleStage());
      expect(run.status).toBe('pending');
      run.start();
      expect(run.status).toBe('running');
    });

    it('starts the first stage', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      expect(run.currentStage.stage).toBe('build');
    });
  });

  describe('next', () => {
    it('returns stage-init when stage not initialized', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      const work = run.next();
      expect(work.kind).toBe('stage-init');
      if (work.kind === 'stage-init') {
        expect(work.stage).toBe('build');
      }
    });

    it('returns task work after init', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      const work = run.next();
      expect(work.kind).toBe('task');
      if (work.kind === 'task') {
        expect(work.task.id).toBe('compile');
      }
    });

    it('returns check work after all tasks completed', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.completeTask();
      const work = run.next();
      expect(work.kind).toBe('check');
      if (work.kind === 'check') {
        expect(work.check.name).toBe('build-ok');
      }
    });

    it('returns complete after all checks passed', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.completeTask();
      run.next(); // check
      run.passCheck({ name: 'build-ok', status: 'pass' as const });
      const work = run.next();
      expect(work.kind).toBe('complete');
    });

    it('returns failed after task fails', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.failTask({ status: 'failed' as const, reason: 'boom' });
      const work = run.next();
      expect(work.kind).toBe('failed');
      if (work.kind === 'failed') {
        expect(work.reason.reason).toBe('task-failed');
      }
    });

    it('returns failed after check fails', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.completeTask();
      run.next(); // check
      run.failCheck({ name: 'build-ok', status: 'fail' as const, message: 'broken' });
      const work = run.next();
      expect(work.kind).toBe('failed');
      if (work.kind === 'failed') {
        expect(work.reason.reason).toBe('check-unrepaired');
      }
    });

    it('returns await-approval when approval required', () => {
      const run = new WorkflowRun('r1', approvalStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.completeTask();
      run.next(); // check
      run.passCheck({ name: 'plan-ok', status: 'pass' as const });
      const work = run.next();
      expect(work.kind).toBe('await-approval');
    });
  });

  describe('retry', () => {
    it('resets a failed task and clears failure', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.failTask({ status: 'failed' as const, reason: 'boom' });
      expect(run.status).toBe('failed');
      expect(run.failure?.reason).toBe('task-failed');

      run.retry();

      expect(run.failure).toBeNull();
      expect(run.currentStage.tasks[0].status).toBe('pending');
      expect(run.status).toBe('running');
    });

    it('resets failed checks and clears failure', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.completeTask();
      run.next(); // check
      run.failCheck({ name: 'build-ok', status: 'fail' as const, message: 'broken' });
      expect(run.status).toBe('failed');

      run.retry();

      expect(run.failure).toBeNull();
      expect(run.currentStage.checks[0].status).toBe('pending');
      expect(run.status).toBe('running');
    });

    it('allows next() to return task work after retrying a failed task', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.failTask({ status: 'failed' as const, reason: 'boom' });
      run.next(); // → failed

      run.retry();

      const work = run.next();
      expect(work.kind).toBe('task');
      if (work.kind === 'task') {
        expect(work.task.id).toBe('compile');
      }
    });

    it('allows completing the stage after retry and re-execution', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.failTask({ status: 'failed' as const, reason: 'boom' });
      run.next(); // → failed

      run.retry();

      run.next(); // task (retried)
      run.completeTask();
      run.next(); // check
      run.passCheck({ name: 'build-ok', status: 'pass' as const });
      const work = run.next();
      expect(work.kind).toBe('complete');
      expect(run.status).toBe('passed');
    });

    it('throws when workflow is not failed', () => {
      const run = new WorkflowRun('r1', singleStage());
      expect(() => run.retry()).toThrow('retry requires failed');
    });
  });

  describe('rerun', () => {
    it('clears all tasks, checks, failure, and approval', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.completeTask();
      run.next(); // check
      run.passCheck({ name: 'build-ok', status: 'pass' as const });
      expect(run.status).toBe('passed');

      run.rerun();

      expect(run.currentStage.tasks.length).toBe(0);
      expect(run.currentStage.checks.length).toBe(0);
      expect(run.currentStage.failure).toBeNull();
      expect(run.currentStage.approval).toBeNull();
      expect(run.currentStage.initialized).toBe(false);
      expect(run.status).toBe('running');
    });

    it('allows full re-initialization after rerun', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.failTask({ status: 'failed' as const, reason: 'boom' });

      run.rerun();

      const work = run.next();
      expect(work.kind).toBe('stage-init');

      run.initTasks();
      const work2 = run.next();
      expect(work2.kind).toBe('task');
    });

    it('can rerun after stage has passed', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.completeTask();
      run.next(); // check
      run.passCheck({ name: 'build-ok', status: 'pass' as const });
      run.next(); // complete

      run.rerun();

      expect(run.status).toBe('running');
      const work = run.next();
      expect(work.kind).toBe('stage-init');
    });

    it('allows completing the stage after rerun', () => {
      const run = new WorkflowRun('r1', singleStage());
      run.start();
      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.failTask({ status: 'failed' as const, reason: 'boom' });

      run.rerun();

      run.next(); // stage-init
      run.initTasks();
      run.next(); // task
      run.completeTask();
      run.next(); // check
      run.passCheck({ name: 'build-ok', status: 'pass' as const });
      const work = run.next();
      expect(work.kind).toBe('complete');
      expect(run.status).toBe('passed');
    });
  });

  describe('multi-stage', () => {
    it('advances to next stage after passStage', () => {
      const run = new WorkflowRun('r1', twoStages());
      run.start();
      run.next(); // stage-init plan
      run.initTasks();
      run.next(); // task plan
      run.completeTask();
      run.next(); // check plan
      run.passCheck({ name: 'plan-ok', status: 'pass' as const });
      const work = run.next(); // complete plan → passStage → stage-init build
      expect(work.kind).toBe('stage-init');
      if (work.kind === 'stage-init') {
        expect(work.stage).toBe('build');
      }
      expect(run.currentStage.stage).toBe('build');
    });
  });
});
