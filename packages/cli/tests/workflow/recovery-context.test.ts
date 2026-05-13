import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { EventBus } from '../../src/services/event-bus';
import { Stage, IssueStatus, type Issue } from '../../src/types';
import type { StageContext } from '../../src/workflow/stage-context';

const executeMock = vi.fn();
const closeMock = vi.fn();
const createMock = vi.fn();

vi.mock('../../src/agent-runtime/agent-session', () => ({
  AgentSession: {
    create: createMock,
  },
}));

vi.mock('../../src/agent-runtime', () => ({
  createWorkflowSessionObservers: vi.fn().mockReturnValue([]),
}));

function makeIssue(stage: Stage): Issue {
  return {
    id: 'issue-ctx',
    number: 199,
    title: 'Unify session context',
    body: 'Every agent session must receive issue and OpenSpec context.',
    stage,
    status: IssueStatus.Active,
    projectId: 'project-1',
    labels: [],
    priority: 'p1',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
  };
}

function makeChangeDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-recovery-context-'));
  fs.mkdirSync(path.join(dir, 'specs', 'workflow'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'proposal.md'), '# Proposal');
  fs.writeFileSync(path.join(dir, 'design.md'), '# Design');
  fs.writeFileSync(path.join(dir, 'specs', 'workflow', 'spec.md'), '# Spec');
  fs.writeFileSync(path.join(dir, 'tasks.json'), '{"tasks":[]}');
  fs.writeFileSync(path.join(dir, 'review.md'), '# Review');
  return dir;
}

function makeContext(stage: Stage, changeDir: string): StageContext {
  return {
    issue: makeIssue(stage),
    acpOptions: { cwd: '/tmp/worktree', model: 'test-model' } as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue(changeDir),
    } as any,
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: new EventBus() as any,
    checkpointManager: {} as any,
    issueRepo: {} as any,
  };
}

describe('recovery task prompts', () => {
  let changeDir: string;

  beforeEach(() => {
    changeDir = makeChangeDir();
    executeMock.mockReset();
    closeMock.mockReset();
    createMock.mockReset();
    createMock.mockResolvedValue({
      execute: executeMock,
      close: closeMock,
    });
    executeMock.mockResolvedValue({ success: true, text: 'done', acpSessionId: 'ses-context' });
    closeMock.mockResolvedValue(undefined);
  });

  afterEach(() => {
    fs.rmSync(changeDir, { recursive: true, force: true });
  });

  it('provides issue and OpenSpec @file context to review fixes', async () => {
    const { runReviewFixTask } = await import('../../src/workflow/review-fix-task');
    await runReviewFixTask(makeContext(Stage.Check, changeDir), {
      worktreePath: '/tmp/worktree',
      failedCheck: {
        name: 'review-passed',
        status: 'fail',
        output: {
          verdict: 'FAIL',
          reviewReport: 'Missing spec coverage.',
          fixSuggestions: 'Update the workflow runner.',
        },
      },
      attempt: 1,
    });

    const prompt = executeMock.mock.calls[0][0] as string;
    expect(prompt).toContain('Issue #199: Unify session context');
    expect(prompt).toContain('Every agent session must receive issue and OpenSpec context.');
    expect(prompt).toContain(`@${path.join(changeDir, 'proposal.md')}`);
    expect(prompt).toContain(`@${path.join(changeDir, 'design.md')}`);
    expect(prompt).toContain(`@${path.join(changeDir, 'specs', 'workflow', 'spec.md')}`);
    expect(prompt).toContain(`@${path.join(changeDir, 'tasks.json')}`);
    expect(prompt).toContain(`@${path.join(changeDir, 'review.md')}`);
  });

  it('provides issue and OpenSpec @file context to plan repair', async () => {
    const { runPlanRepairTask } = await import('../../src/workflow/plan-repair-task');
    await runPlanRepairTask(makeContext(Stage.Plan, changeDir), {
      worktreePath: '/tmp/worktree',
      failedCheck: {
        name: 'self-review-passed',
        status: 'fail',
        message: 'self-review reported missing requirements',
      },
      attempt: 1,
    });

    const prompt = executeMock.mock.calls[0][0] as string;
    expect(prompt).toContain('Issue #199: Unify session context');
    expect(prompt).toContain('Every agent session must receive issue and OpenSpec context.');
    expect(prompt).toContain(`@${path.join(changeDir, 'proposal.md')}`);
    expect(prompt).toContain(`@${path.join(changeDir, 'design.md')}`);
    expect(prompt).toContain(`@${path.join(changeDir, 'specs', 'workflow', 'spec.md')}`);
    expect(prompt).toContain(`@${path.join(changeDir, 'tasks.json')}`);
  });
});

