import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import { resetDatabase, closeDatabase } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { Stage, type Issue } from '../src/types';
import { createSpawnCoderTool } from '../src/tools/spawn-coder';
import { createReadWorkflowTool } from '../src/tools/read-workflow';
import { createAdvanceStageTool } from '../src/tools/advance-stage';
import { createAddCommentTool } from '../src/tools/add-comment';
import { createGetIssueTool } from '../src/tools/get-issue';
import { createReadPrdTool } from '../src/tools/read-prd';
import { createReadSpecTool } from '../src/tools/read-spec';
import { createStoreLearningTool, createLoadLearningsTool } from '../src/tools/session-memory';
import { createUpdateTaskStatusTool, createGetTaskStatusTool } from '../src/tools/task-status';
import { createSelfReviewTool, createGeneratePrdTool } from '../src/tools/self-review';
import { ToolRegistry } from '../src/agent-runtime/tool';

const mockState = vi.hoisted(() => {
  const spawnCalls: Array<{
    command: string;
    args: string[];
    options: Record<string, unknown>;
  }> = [];
  const killedPids: number[] = [];
  let acpPromptCount = 0;
  let nextPid = 50000;

  return { spawnCalls, killedPids, acpPromptCount, nextPid };
});

vi.mock('child_process', () => ({
  spawn: vi.fn((command: string, args: string[], options: Record<string, unknown>) => {
    mockState.spawnCalls.push({ command, args, options });
    const pid = mockState.nextPid++;
    const proc = new EventEmitter() as any;
    proc.pid = pid;
    proc.stdin = new PassThrough();
    proc.stdout = new PassThrough();
    proc.stderr = new PassThrough();
    proc.kill = vi.fn((signal?: string) => {
      mockState.killedPids.push(pid);
      proc.emit('exit', 0, signal || 'SIGTERM');
      return true;
    });
    proc.disconnected = false;
    setTimeout(() => proc.emit('spawn'), 0);
    return proc;
  }),
}));

vi.mock('@agentclientprotocol/sdk', () => ({
  ClientSideConnection: vi.fn().mockImplementation((handler: any) => {
    const handlerObj = typeof handler === 'function' ? handler({}) : handler;
    return {
      initialize: vi.fn().mockResolvedValue({ protocolVersion: '2025-01-01' }),
      newSession: vi.fn().mockResolvedValue({ sessionId: `mock-session-${mockState.acpPromptCount}` }),
      prompt: vi.fn().mockImplementation(async (params: any) => {
        const taskText = params.prompt?.[0]?.text || '';
        const lower = taskText.toLowerCase();
        let output = 'Task completed successfully.';
        if (taskText.includes('\u68c0\u67e5') || (lower.includes('check') && !lower.includes('\u5b9e\u73b0'))) {
          output = [
            'Check results:',
            '- TypeScript: no errors',
            '- ESLint: no warnings',
            '- Tests: 12/12 passing',
            '- Coverage: 87%',
            'No issues found.',
          ].join('\n');
        } else if (taskText.includes('\u5206\u6790') && !taskText.includes('\u68c0\u67e5')) {
          output = [
            'Plan for the issue:',
            '1. Create data models and database schema',
            '2. Implement API endpoints',
            '3. Add input validation',
            '4. Write unit tests',
          ].join('\n');
        } else if (taskText.includes('\u5b9e\u73b0')) {
          output = [
            'Build completed:',
            '- Created User model with validation',
            '- Implemented POST /api/users endpoint',
            '- Added Zod schema validation',
            '- All 12 tests passing',
          ].join('\n');
        } else if (lower.includes('check')) {
          output = [
            'Check results:',
            '- TypeScript: no errors',
            '- Tests: 12/12 passing',
            'No issues found.',
          ].join('\n');
        } else if (lower.includes('build') || lower.includes('implement')) {
          output = [
            'Build completed:',
            '- Created User model with validation',
            '- All 12 tests passing',
          ].join('\n');
        } else if (lower.includes('plan') || lower.includes('analyze')) {
          output = [
            'Plan for the issue:',
            '1. Create data models',
            '2. Implement API endpoints',
            '3. Write unit tests',
          ].join('\n');
        }
        mockState.acpPromptCount++;
        if (handlerObj?.sessionUpdate) {
          await handlerObj.sessionUpdate({
            update: {
              sessionUpdate: 'agent_message_chunk',
              content: { text: output },
            },
          });
        }
        return {};
      }),
      cancel: vi.fn().mockResolvedValue({}),
    };
  }),
  ndJsonStream: vi.fn().mockReturnValue({}),
  PROTOCOL_VERSION: '2025-01-01',
}));

describe('Agent Workflow E2E', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let commentRepo: CommentRepo;
  let projectId: string;
  let issue: Issue;

  beforeEach(() => {
    mockState.spawnCalls.length = 0;
    mockState.killedPids.length = 0;
    mockState.acpPromptCount = 0;
    mockState.nextPid = 50000;

    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);

    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    commentRepo = new CommentRepo(db);

    const configRepo = new ConfigRepo(db);

    const project = projectRepo.create({ name: 'E2E Test', path: '/tmp/e2e-test' });
    projectId = project.id;

    const nextNumber = issueRepo.getNextNumber(projectId);
    issue = issueRepo.create({
      number: nextNumber,
      projectId,
      title: 'Implement user authentication',
      body: 'Add user model, auth service, and login endpoint',
    });
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('Tool Integration', () => {
    it('read_workflow returns workflow with stages', async () => {
      const tool = createReadWorkflowTool({ cwd: '/tmp/e2e-test' });
      const result = await tool.definition.execute({});

      expect(result).toContain('# Workflow');
      expect(result).toContain('## plan');
      expect(result).toContain('## build');
      expect(result).toContain('## check');
      expect(result).toContain('approval: true');
    });

    it('spawn_coder spawns subprocess and returns meaningful result', async () => {
      const tool = createSpawnCoderTool({ worktreePath: '/tmp/e2e-test' });
      const result = await tool.definition.execute({
        taskTemplate: '\u5206\u6790 issue #{issue.number}: {issue.title}',
        variables: { issue: { number: issue.number, title: issue.title } },
      });

      expect(result).toContain('Plan for the issue:');
      expect(result).toContain('Create data models');
      expect(mockState.spawnCalls.length).toBe(1);
      expect(mockState.spawnCalls[0].command).toBe('opencode');
      expect(mockState.spawnCalls[0].args).toEqual(['acp']);
      expect(mockState.spawnCalls[0].options.cwd).toBe('/tmp/e2e-test');
    });

    it('spawn_coder replaces template variables correctly', async () => {
      const tool = createSpawnCoderTool({ worktreePath: '/tmp/e2e-test' });
      const result = await tool.definition.execute({
        taskTemplate: 'Implement {issue.title}. Plan: {plan.output}',
        variables: {
          issue: { title: 'User auth', number: 1 },
          plan: { output: 'Step 1, Step 2' },
        },
      });

      expect(result).toContain('Build completed:');
    });

    it('spawn_coder handles missing cwd with error', async () => {
      const tool = createSpawnCoderTool();
      const result = await tool.definition.execute({
        taskTemplate: 'Do something',
        variables: {},
      });

      expect(result).toContain('Error');
      expect(mockState.spawnCalls.length).toBe(0);
    });

    it('advance_stage transitions draft to plan', async () => {
      const tool = createAdvanceStageTool({ issue, issueRepo });
      const result = await tool.definition.execute({ stage: 'plan' });

      expect(result).toContain('advanced');
      expect(result).toContain('draft');
      expect(result).toContain('plan');

      const updated = issueRepo.findById(issue.id);
      expect(updated?.stage).toBe(Stage.Plan);
    });

    it('advance_stage rejects invalid transitions', async () => {
      const tool = createAdvanceStageTool({ issue, issueRepo });
      const result = await tool.definition.execute({ stage: 'done' });

      expect(result).toContain('Error');
      expect(result).toContain('cannot advance');
    });

    it('add_comment creates comment in database', async () => {
      const tool = createAddCommentTool({ issue, commentRepo });
      const result = await tool.definition.execute({
        body: 'Plan completed successfully',
      });

      expect(result).toContain('Comment added');

      const comments = commentRepo.findByIssue(issue.id);
      expect(comments).toHaveLength(1);
      expect(comments[0].body).toBe('Plan completed successfully');
    });

    it('get_issue returns current issue state', async () => {
      const tool = createGetIssueTool({ issue, issueRepo });
      const result = await tool.definition.execute({});

      expect(result).toContain(`Issue #${issue.number}`);
      expect(result).toContain(issue.title);
      expect(result).toContain(Stage.Draft);
    });

    it('get_issue reflects stage changes', async () => {
      issueRepo.updateStage(issue.id, Stage.Plan);

      const tool = createGetIssueTool({ issue, issueRepo });
      const result = await tool.definition.execute({});

      expect(result).toContain(Stage.Plan);
    });
  });

  describe('Stage Transitions', () => {
    it('plan -> build -> check -> done full progression', async () => {
      issueRepo.updateStage(issue.id, Stage.Plan);
      let currentIssue = issueRepo.findById(issue.id)!;

      const advanceToBuild = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      await advanceToBuild.definition.execute({ stage: 'build' });
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Build);

      currentIssue = issueRepo.findById(issue.id)!;
      const advanceToCheck = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      await advanceToCheck.definition.execute({ stage: 'check' });
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Check);

      currentIssue = issueRepo.findById(issue.id)!;
      const advanceToDone = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      await advanceToDone.definition.execute({ stage: 'done' });
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Done);
    });

    it('check can loop back to plan', async () => {
      issueRepo.updateStage(issue.id, Stage.Check);
      const currentIssue = issueRepo.findById(issue.id)!;

      const tool = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      const result = await tool.definition.execute({ stage: 'plan' });

      expect(result).toContain('advanced');
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Plan);
    });

    it('rejects all transitions from done', async () => {
      issueRepo.updateStage(issue.id, Stage.Done);
      const currentIssue = issueRepo.findById(issue.id)!;

      const tool = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      const result = await tool.definition.execute({ stage: 'plan' });

      expect(result).toContain('Error');
    });
  });

  describe('Simulated Agent Flow', () => {
    it('completes plan -> build -> check -> done without crashes', async () => {
      issueRepo.updateStage(issue.id, Stage.Plan);

      const spawnCoder = createSpawnCoderTool({ worktreePath: '/tmp/e2e-test' });
      const readWorkflow = createReadWorkflowTool({ cwd: '/tmp/e2e-test' });

      const workflow = await readWorkflow.definition.execute({});
      expect(workflow).toContain('## plan');

      const planResult = await spawnCoder.definition.execute({
        taskTemplate: '\u5206\u6790 issue #{issue.number}: {issue.title}\u3001\u63a2\u7d22 codebase\u3001\u4ea7\u51fa\u5b9e\u73b0\u8ba1\u5212',
        variables: { issue: { number: issue.number, title: issue.title } },
      });
      expect(planResult).toContain('Plan for the issue:');

      let currentIssue = issueRepo.findById(issue.id)!;
      const advanceBuild = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      const buildResult = await advanceBuild.definition.execute({ stage: 'build' });
      expect(buildResult).toContain('advanced');
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Build);

      const buildOutput = await spawnCoder.definition.execute({
        taskTemplate: '\u6309 plan \u9636\u6bb5\u7684\u8ba1\u5212\u5b9e\u73b0 {issue.title}\u3002\u8ba1\u5212\u6458\u8981\uff1a{plan.output}',
        variables: {
          issue: { title: issue.title },
          plan: { output: planResult },
        },
      });
      expect(buildOutput).toContain('Build completed:');

      currentIssue = issueRepo.findById(issue.id)!;
      const advanceCheck = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      const checkResult = await advanceCheck.definition.execute({ stage: 'check' });
      expect(checkResult).toContain('advanced');
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Check);

      const checkOutput = await spawnCoder.definition.execute({
        taskTemplate: '\u68c0\u67e5 {issue.title} \u7684\u5b9e\u73b0\uff1a\u8fd0\u884c\u6d4b\u8bd5\u3001lint\u3001typecheck\uff0c\u62a5\u544a\u95ee\u9898',
        variables: { issue: { title: issue.title } },
      });
      expect(checkOutput).toContain('Check results:');
      expect(checkOutput).toContain('12/12 passing');

      currentIssue = issueRepo.findById(issue.id)!;
      const advanceDone = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      const doneResult = await advanceDone.definition.execute({ stage: 'done' });
      expect(doneResult).toContain('advanced');
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Done);
    });

    it('spawn_coder called once per stage with correct cwd', async () => {
      issueRepo.updateStage(issue.id, Stage.Plan);

      const spawnCoder = createSpawnCoderTool({ worktreePath: '/tmp/e2e-test' });

      await spawnCoder.definition.execute({
        taskTemplate: 'Plan task',
        variables: {},
      });
      await spawnCoder.definition.execute({
        taskTemplate: 'Build task',
        variables: {},
      });
      await spawnCoder.definition.execute({
        taskTemplate: 'Check task',
        variables: {},
      });

      expect(mockState.spawnCalls.length).toBe(3);
      for (const call of mockState.spawnCalls) {
        expect(call.command).toBe('opencode');
        expect(call.args).toEqual(['acp']);
        expect(call.options.cwd).toBe('/tmp/e2e-test');
      }
    });

    it('subprocess is killed after each spawn_coder call', async () => {
      issueRepo.updateStage(issue.id, Stage.Plan);

      const spawnCoder = createSpawnCoderTool({ worktreePath: '/tmp/e2e-test' });

      await spawnCoder.definition.execute({
        taskTemplate: 'Plan task',
        variables: {},
      });

      expect(mockState.killedPids.length).toBe(1);
      expect(mockState.killedPids[0]).toBe(50000);

      await spawnCoder.definition.execute({
        taskTemplate: 'Build task',
        variables: {},
      });

      expect(mockState.killedPids.length).toBe(2);
      expect(mockState.killedPids[1]).toBe(50001);
    });

    it('approval flow: build stage adds comment and does not auto-advance', async () => {
      issueRepo.updateStage(issue.id, Stage.Plan);

      const spawnCoder = createSpawnCoderTool({ worktreePath: '/tmp/e2e-test' });
      const readWorkflow = createReadWorkflowTool({ cwd: '/tmp/e2e-test' });
      const addComment = createAddCommentTool({ issue, commentRepo });

      const workflow = await readWorkflow.definition.execute({});
      expect(workflow).toContain('## build');
      expect(workflow).toContain('approval: true');

      await spawnCoder.definition.execute({
        taskTemplate: '\u5206\u6790 issue',
        variables: { issue: { number: issue.number, title: issue.title } },
      });

      let currentIssue = issueRepo.findById(issue.id)!;
      const advanceBuild = createAdvanceStageTool({ issue: currentIssue, issueRepo });
      await advanceBuild.definition.execute({ stage: 'build' });

      currentIssue = issueRepo.findById(issue.id)!;
      await spawnCoder.definition.execute({
        taskTemplate: '\u5b9e\u73b0 {issue.title}',
        variables: { issue: { title: issue.title } },
      });

      currentIssue = issueRepo.findById(issue.id)!;
      const commentTool = createAddCommentTool({ issue: currentIssue, commentRepo });
      await commentTool.definition.execute({
        body: 'Build completed. Waiting for approval before proceeding to check.',
      });

      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Build);

      const comments = commentRepo.findByIssue(issue.id);
      expect(comments.some((c) => c.body.includes('approval'))).toBe(true);
    });

    it('stages advance correctly with fresh tool instances', async () => {
      issueRepo.updateStage(issue.id, Stage.Plan);

      function advanceTo(target: string) {
        const current = issueRepo.findById(issue.id)!;
        return createAdvanceStageTool({ issue: current, issueRepo });
      }

      await advanceTo('build').definition.execute({ stage: 'build' });
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Build);

      await advanceTo('check').definition.execute({ stage: 'check' });
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Check);

      await advanceTo('done').definition.execute({ stage: 'done' });
      expect(issueRepo.findById(issue.id)?.stage).toBe(Stage.Done);
    });
  });

  describe('Tool Registry', () => {
    it('all agent tools register correctly', () => {
      const registry = new ToolRegistry();

      registry.register(createSpawnCoderTool({ worktreePath: '/tmp/test' }));
      registry.register(createReadWorkflowTool({ cwd: '/tmp/test' }));
      registry.register(
        createAdvanceStageTool({ issue, issueRepo })
      );
      registry.register(createAddCommentTool({ issue, commentRepo }));
      registry.register(createGetIssueTool({ issue, issueRepo }));
      registry.register(createReadPrdTool({ projectPath: '/tmp/test' }));
      registry.register(createReadSpecTool({ projectPath: '/tmp/test' }));
      registry.register(createStoreLearningTool({ projectPath: '/tmp/test' }));
      registry.register(createLoadLearningsTool({ projectPath: '/tmp/test' }));
      registry.register(createUpdateTaskStatusTool({ projectPath: '/tmp/test' }));
      registry.register(createGetTaskStatusTool({ projectPath: '/tmp/test' }));
      registry.register(createSelfReviewTool({ projectPath: '/tmp/test' }));
      registry.register(createGeneratePrdTool({ projectPath: '/tmp/test' }));

      const toolSet = registry.toToolSet();
      expect(Object.keys(toolSet)).toEqual(
        expect.arrayContaining([
          'spawn_coder',
          'read_workflow',
          'advance_stage',
          'add_comment',
          'get_issue',
          'read_prd',
          'read_spec',
          'store_learning',
          'load_learnings',
          'update_task_status',
          'get_task_status',
          'run_self_review',
          'generate_prd',
        ])
      );
      expect(Object.keys(toolSet)).toHaveLength(13);
    });
  });

  describe('Error Handling', () => {
    it('spawn_coder with empty template returns result', async () => {
      const tool = createSpawnCoderTool({ worktreePath: '/tmp/e2e-test' });
      const result = await tool.definition.execute({
        taskTemplate: '',
        variables: {},
      });

      expect(result).toContain('Task completed');
    });

    it('spawn_coder custom cwd overrides default', async () => {
      const tool = createSpawnCoderTool({ worktreePath: '/tmp/default' });
      await tool.definition.execute({
        taskTemplate: 'Task',
        variables: {},
        cwd: '/tmp/custom',
      });

      expect(mockState.spawnCalls[0].options.cwd).toBe('/tmp/custom');
    });

    it('get_issue on deleted issue returns error', async () => {
      issueRepo.delete(issue.id);

      const tool = createGetIssueTool({ issue, issueRepo });
      const result = await tool.definition.execute({});

      expect(result).toContain('Error');
      expect(result).toContain('not found');
    });
  });
});
