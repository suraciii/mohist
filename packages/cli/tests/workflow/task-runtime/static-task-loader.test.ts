import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Stage, IssueStatus } from '../../../src/types';
import type { StageContext } from '../../../src/workflow/stage-context';
import type { StaticTaskDefinition, StaticTaskResolver } from '../../../src/workflow/task-runtime/static-task-loader';
import { StaticTaskLoader } from '../../../src/workflow/task-runtime/static-task-loader';

function makeContext(): StageContext {
  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
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
    emit: vi.fn(),
    log: vi.fn(),
  };
}

describe('StaticTaskLoader', () => {
  it('converts static definitions into executable tasks with resolved prompts', () => {
    const definitions: StaticTaskDefinition[] = [
      { taskId: 'plan:proposal', title: 'Generate proposal', kind: 'agent-session' },
      { taskId: 'plan:specs', title: 'Generate specs', kind: 'agent-session' },
      { taskId: 'plan:design', title: 'Generate design', kind: 'agent-session' },
    ];

    const resolvers: Partial<Record<'agent-session', StaticTaskResolver>> = {
      'agent-session': {
        resolvePrompt: (taskId: string, _ctx: StageContext) => {
          if (taskId === 'plan:proposal') return 'Generate the proposal artifact';
          if (taskId === 'plan:specs') return 'Generate the specs artifact';
          if (taskId === 'plan:design') return 'Generate the design artifact';
          return 'Default prompt';
        },
      },
    };

    const loader = new StaticTaskLoader(definitions, resolvers);
    const ctx = makeContext();
    const executableTasks = loader.load(ctx);

    expect(executableTasks).toHaveLength(3);
    expect(executableTasks[0]).toMatchObject({
      taskId: 'plan:proposal',
      title: 'Generate proposal',
      kind: 'agent-session',
      prompt: 'Generate the proposal artifact',
    });
    expect(executableTasks[1]).toMatchObject({
      taskId: 'plan:specs',
      title: 'Generate specs',
      kind: 'agent-session',
      prompt: 'Generate the specs artifact',
    });
    expect(executableTasks[2]).toMatchObject({
      taskId: 'plan:design',
      title: 'Generate design',
      kind: 'agent-session',
      prompt: 'Generate the design artifact',
    });
  });

  it('resolves input from StageContext when resolveInput is provided', () => {
    const definitions: StaticTaskDefinition[] = [
      {
        taskId: 'integrate:merge',
        title: 'Merge to main',
        kind: 'service-call',
        resolveInput: (ctx: StageContext) => ({
          targetBranch: 'main',
          issueNumber: ctx.issue.number,
        }),
      },
    ];

    const loader = new StaticTaskLoader(definitions, {});
    const ctx = makeContext();
    const executableTasks = loader.load(ctx);

    expect(executableTasks).toHaveLength(1);
    expect(executableTasks[0].input).toEqual({
      targetBranch: 'main',
      issueNumber: 159,
    });
  });

  it('returns executable tasks in the same order as supplied static definitions', () => {
    const definitions: StaticTaskDefinition[] = [
      { taskId: 'check:review', title: 'AI review', kind: 'agent-session' },
      { taskId: 'check:self-review', title: 'Self review check', kind: 'agent-session' },
      { taskId: 'integrate:spec-sync', title: 'Sync specs', kind: 'service-call' },
      { taskId: 'integrate:merge', title: 'Merge', kind: 'service-call' },
    ];

    const loader = new StaticTaskLoader(definitions, {});
    const ctx = makeContext();
    const executableTasks = loader.load(ctx);

    expect(executableTasks[0].taskId).toBe('check:review');
    expect(executableTasks[1].taskId).toBe('check:self-review');
    expect(executableTasks[2].taskId).toBe('integrate:spec-sync');
    expect(executableTasks[3].taskId).toBe('integrate:merge');
  });

  it('does not add build dynamic ordering, dependsOn, checkpoint, or Ralph execution behavior', () => {
    const definitions: StaticTaskDefinition[] = [
      { taskId: 'build:compile', title: 'Compile', kind: 'agent-session' },
      { taskId: 'build:test', title: 'Test', kind: 'agent-session' },
    ];

    const loader = new StaticTaskLoader(definitions, {});
    const ctx = makeContext();
    const executableTasks = loader.load(ctx);

    for (const task of executableTasks) {
      expect(task).not.toHaveProperty('dependsOn');
      expect(task).not.toHaveProperty('checkpoint');
      expect(task).not.toHaveProperty('dynamic');
      expect(task).not.toHaveProperty('order');
    }
  });

  it('handles missing resolver gracefully by leaving prompt undefined', () => {
    const definitions: StaticTaskDefinition[] = [
      { taskId: 'unknown:task', title: 'Unknown task', kind: 'agent-session' },
    ];

    const loader = new StaticTaskLoader(definitions, {});
    const ctx = makeContext();
    const executableTasks = loader.load(ctx);

    expect(executableTasks[0].prompt).toBeUndefined();
    expect(executableTasks[0].input).toBeUndefined();
  });

  it('does not modify the original definitions array', () => {
    const definitions: StaticTaskDefinition[] = [
      { taskId: 'plan:proposal', title: 'Generate proposal', kind: 'agent-session' },
    ];

    const loader = new StaticTaskLoader(definitions, {});
    const ctx = makeContext();
    loader.load(ctx);

    expect(definitions[0].taskId).toBe('plan:proposal');
    expect(definitions[0].title).toBe('Generate proposal');
  });

  it('loader does not own checkpoint, stage transitions, or approval decisions', () => {
    const definitions: StaticTaskDefinition[] = [
      { taskId: 'plan:proposal', title: 'Generate proposal', kind: 'agent-session' },
    ];

    const loader = new StaticTaskLoader(definitions, {});
    const ctx = makeContext();
    const executableTasks = loader.load(ctx);

    for (const task of executableTasks) {
      expect(task).not.toHaveProperty('checkpoint');
      expect(task).not.toHaveProperty('stageTransition');
      expect(task).not.toHaveProperty('approval');
    }
  });

  describe('Plan static definitions', () => {
    it('expresses Plan artifact tasks as static definitions', () => {
      const planDefinitions: StaticTaskDefinition[] = [
        { taskId: 'plan:proposal', title: 'proposal.md', kind: 'agent-session' },
        { taskId: 'plan:specs', title: 'specs/', kind: 'agent-session' },
        { taskId: 'plan:design', title: 'design.md', kind: 'agent-session' },
        { taskId: 'plan:tasks', title: 'tasks.json', kind: 'agent-session' },
        { taskId: 'plan:self-review', title: 'self-review.md', kind: 'agent-session' },
      ];

      const planResolvers: Partial<Record<'agent-session', StaticTaskResolver>> = {
        'agent-session': {
          resolvePrompt: (taskId: string, _ctx: StageContext) => `Build prompt for ${taskId}`,
        },
      };

      const loader = new StaticTaskLoader(planDefinitions, planResolvers);
      const ctx = makeContext();
      const executableTasks = loader.load(ctx);

      expect(executableTasks).toHaveLength(5);
      expect(executableTasks.map((t) => t.taskId)).toEqual([
        'plan:proposal',
        'plan:specs',
        'plan:design',
        'plan:tasks',
        'plan:self-review',
      ]);
    });
  });

  describe('Check static definitions', () => {
    it('expresses Check review tasks as static definitions without runner-specific branching', () => {
      const checkDefinitions: StaticTaskDefinition[] = [
        { taskId: 'check:review', title: 'AI review', kind: 'agent-session' },
        { taskId: 'check:self-review', title: 'Self review check', kind: 'agent-session' },
      ];

      const checkResolvers: Partial<Record<'agent-session', StaticTaskResolver>> = {
        'agent-session': {
          resolvePrompt: (taskId: string, _ctx: StageContext) => {
            if (taskId === 'check:review') return 'Run AI code review';
            if (taskId === 'check:self-review') return 'Run self review verification';
            return 'Default';
          },
        },
      };

      const loader = new StaticTaskLoader(checkDefinitions, checkResolvers);
      const ctx = makeContext();
      const executableTasks = loader.load(ctx);

      expect(executableTasks).toHaveLength(2);
      expect(executableTasks[0].prompt).toBe('Run AI code review');
      expect(executableTasks[1].prompt).toBe('Run self review verification');
    });
  });

  describe('Integrate static definitions', () => {
    it('expresses Integrate service-call tasks as static definitions', () => {
      const integrateDefinitions: StaticTaskDefinition[] = [
        {
          taskId: 'integrate:spec-sync',
          title: 'Sync spec',
          kind: 'service-call',
          resolveInput: (ctx: StageContext) => ({
            issueNumber: ctx.issue.number,
            changeDir: '/tmp/change',
          }),
        },
        {
          taskId: 'integrate:archive-change',
          title: 'Archive change',
          kind: 'service-call',
          resolveInput: (ctx: StageContext) => ({
            issueNumber: ctx.issue.number,
          }),
        },
        {
          taskId: 'integrate:merge',
          title: 'Merge to main',
          kind: 'service-call',
          resolveInput: (_ctx: StageContext) => ({
            targetBranch: 'main',
          }),
        },
      ];

      const loader = new StaticTaskLoader(integrateDefinitions, {});
      const ctx = makeContext();
      const executableTasks = loader.load(ctx);

      expect(executableTasks).toHaveLength(3);
      expect(executableTasks[0].kind).toBe('service-call');
      expect(executableTasks[0].input).toEqual({ issueNumber: 159, changeDir: '/tmp/change' });
      expect(executableTasks[1].input).toEqual({ issueNumber: 159 });
      expect(executableTasks[2].input).toEqual({ targetBranch: 'main' });
    });
  });
});