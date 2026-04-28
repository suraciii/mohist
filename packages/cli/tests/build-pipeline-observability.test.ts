import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { Stage, type Issue } from '../src/types';
import { DatabaseManager } from '../src/db/database';
import { IssueRepo } from '../src/db/issue-repo';
import { WorkflowLogRepo } from '../src/db/workflow-log-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectRepo } from '../src/db/project-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { EventBus } from '../src/services/event-bus';
import { StateManager } from '../src/server/state-manager';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { createIssueRoutes } from '../src/api/issues';
import type { ChangeArtifactsManager } from '../src/workflow/workflow-controller';
import type { OpenSpecChange } from '../src/openspec/detector';

vi.mock('../src/agent-runtime/acp-session', () => ({
  createAcpConnection: vi.fn(),
}));

let mockDetectResult: OpenSpecChange | null = null;
let mockRalphExecuteResult: any = {
  success: true,
  completed: 1,
  failed: 0,
  total: 1,
  taskResults: [],
};

vi.mock('../src/openspec/detector', () => ({
  detectOpenSpecChange: vi.fn().mockImplementation(() => mockDetectResult),
}));

vi.mock('../src/openspec/ralph-executor', () => ({
  RalphExecutor: vi.fn().mockImplementation(() => ({
    execute: vi.fn().mockImplementation(() => Promise.resolve(mockRalphExecuteResult)),
  })),
  setAcpSessionRunner: vi.fn(),
  resetAcpSessionRunner: vi.fn(),
}));

vi.mock('fs', () => ({
  existsSync: vi.fn().mockReturnValue(true),
  readdirSync: vi.fn().mockReturnValue([]),
  rmSync: vi.fn(),
  mkdirSync: vi.fn(),
  writeFileSync: vi.fn(),
  readFileSync: vi.fn(),
}));

vi.mock('../src/agents/artifact-prompt', () => ({
  buildArtifactPrompt: vi.fn().mockReturnValue('mock-prompt'),
  buildSelfReviewPrompt: vi.fn().mockReturnValue('mock-self-review-prompt'),
  buildReviewerPrompt: vi.fn().mockReturnValue('mock-reviewer-prompt'),
  buildReviewSelfCheckPrompt: vi.fn().mockReturnValue('mock-review-self-check-prompt'),
}));

import type { PipelineCheckpointRepo } from '../src/db/pipeline-checkpoint-repo';
import { WorkflowController } from '../src/workflow/workflow-controller';

function createMockIssue(stage: Stage, overrides?: Partial<Issue>): Issue {
  return {
    id: 'issue-1',
    number: 1,
    title: 'Test Issue',
    body: 'Test body',
    stage,
    status: 'active' as any,
    projectId: 'proj-1',
    labels: [],
    createdAt: '2024-01-01T00:00:00Z',
    updatedAt: '2024-01-01T00:00:00Z',
    ...overrides,
  };
}

function createMockArtifactManager(): ChangeArtifactsManager {
  return {
    getChangeDir: vi.fn().mockReturnValue('/tmp/change'),
    createChangeDir: vi.fn().mockReturnValue('/tmp/change'),
    readArtifact: vi.fn().mockReturnValue(null),
    writeArtifact: vi.fn().mockReturnValue(true),
    exists: vi.fn().mockReturnValue(true),
    readTasks: vi.fn().mockReturnValue(null),
    updateTaskPasses: vi.fn().mockReturnValue(true),
  };
}

function createMockRepos() {
  return {
    issueRepo: {
      findById: vi.fn(),
      findAll: vi.fn().mockReturnValue([]),
      create: vi.fn(),
      update: vi.fn(),
      remove: vi.fn(),
      updateStage: vi.fn().mockImplementation((_id: string, stage: Stage) => createMockIssue(stage)),
      setApprovalState: vi.fn(),
      clearApprovalState: vi.fn(),
      findPendingApprovalByIssueId: vi.fn().mockReturnValue(null),
      findByProjectId: vi.fn().mockReturnValue([]),
    } as unknown as IssueRepo,
    eventBus: {
      on: vi.fn(),
      off: vi.fn(),
      emit: vi.fn(),
      removeAllListeners: vi.fn(),
      emitPersistent: vi.fn(),
    } as unknown as EventBus,
  };
}

function createTestServer(app: Hono): http.Server {
  return http.createServer(async (req, res) => {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(chunk);
    const bodyStr = chunks.length > 0 ? Buffer.concat(chunks).toString() : undefined;
    const initHeaders: Record<string, string> = {};
    for (const [key, value] of Object.entries(req.headers)) {
      if (typeof value === 'string') initHeaders[key] = value;
      else if (Array.isArray(value)) initHeaders[key] = value.join(', ');
    }
    const response = await app.fetch(new Request(`http://localhost${req.url}`, {
      method: req.method,
      headers: initHeaders,
      body: bodyStr,
    }));
    res.writeHead(response.status, Object.fromEntries(response.headers.entries()));
    if (response.body) {
      const reader = response.body.getReader();
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        res.write(Buffer.from(value));
      }
    }
    res.end();
  });
}

const FAKE_CHANGE: OpenSpecChange = {
  changePath: '/tmp/change',
  tasksPath: '/tmp/change/tasks.json',
  sessionMemoriesPath: '/tmp/change/session-memories',
  proposalPath: '/tmp/change/proposal.md',
  designPath: '/tmp/change/design.md',
  specsPath: '/tmp/change/specs',
};

describe('Build Pipeline Observability - WorkflowController', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockDetectResult = null;
    mockRalphExecuteResult = {
      success: true,
      completed: 1,
      failed: 0,
      total: 1,
      taskResults: [],
    };
  });

  describe('Zero-work detection', () => {
    it('should return success:false when completed===0 and total>0', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      const tasksJson = JSON.stringify({
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'Task 1', passes: true, attempts: 1 },
          { id: 'T-002', order: 2, title: 'Task 2', passes: true, attempts: 1 },
        ],
      });
      (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

      mockDetectResult = FAKE_CHANGE;
      mockRalphExecuteResult = {
        completed: 0,
        failed: 0,
        total: 2,
        taskResults: [],
        success: true,
      };

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      const result = await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(result.success).toBe(false);
      expect(result.message).toContain('0 tasks executed');
      expect(result.message).toContain('2 total');
    });

    it('should return success:true when completed>0', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      const tasksJson = JSON.stringify({
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'Task 1', passes: false, attempts: 0 },
        ],
      });
      (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

      mockDetectResult = FAKE_CHANGE;
      mockRalphExecuteResult = {
        completed: 1,
        failed: 0,
        total: 1,
        taskResults: [],
        success: true,
      };

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      const result = await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(result.success).toBe(true);
    });
  });

  describe('SSE events', () => {
    it('should emit build_stage_failed SSE when no change found', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      mockDetectResult = null;

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(eventBus.emit).toHaveBeenCalledWith(
        'build_stage_failed',
        expect.objectContaining({ reason: 'no_change_found' }),
      );
    });

    it('should emit build_stage_started and build_tasks_snapshot SSE events', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      const tasksJson = JSON.stringify({
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'Task 1', passes: false, attempts: 0 },
          { id: 'T-002', order: 2, title: 'Task 2', passes: false, attempts: 0 },
        ],
      });
      (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

      mockDetectResult = FAKE_CHANGE;
      mockRalphExecuteResult = {
        completed: 2,
        failed: 0,
        total: 2,
        taskResults: [],
        success: true,
      };

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(eventBus.emit).toHaveBeenCalledWith(
        'build_stage_started',
        expect.objectContaining({ stage: 'build', tasksCount: 2 }),
      );

      expect(eventBus.emit).toHaveBeenCalledWith(
        'build_tasks_snapshot',
        expect.objectContaining({ total: 2, pending: 2, passed: 0 }),
      );
    });

    it('should emit build_stage_completed SSE when build succeeds', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      const tasksJson = JSON.stringify({
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'Task 1', passes: false, attempts: 0 },
        ],
      });
      (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

      mockDetectResult = FAKE_CHANGE;
      mockRalphExecuteResult = {
        completed: 1,
        failed: 0,
        total: 1,
        taskResults: [],
        success: true,
      };

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(eventBus.emit).toHaveBeenCalledWith(
        'build_stage_completed',
        expect.objectContaining({ completed: 1, failed: 0, total: 1 }),
      );
    });

    it('should emit build_stage_failed SSE when build fails with tasks_failed', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      const tasksJson = JSON.stringify({
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'Task 1', passes: false, attempts: 0 },
          { id: 'T-002', order: 2, title: 'Task 2', passes: false, attempts: 0 },
        ],
      });
      (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

      mockDetectResult = FAKE_CHANGE;
      mockRalphExecuteResult = {
        completed: 1,
        failed: 1,
        total: 2,
        taskResults: [],
        success: false,
      };

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(eventBus.emit).toHaveBeenCalledWith(
        'build_stage_failed',
        expect.objectContaining({ reason: 'tasks_failed' }),
      );
    });

    it('should NOT emit zero_work on full checkpoint recovery (completed=total, hadCheckpoint=true)', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      const mockCheckpointRepo = {
        get: vi.fn().mockReturnValue({
          issueNumber: 1,
          stage: 'build',
          completedSteps: ['T-001', 'T-002'],
          nextStep: null,
          updatedAt: '2024-01-01T00:00:00Z',
        }),
        upsert: vi.fn(),
        delete: vi.fn(),
        deleteAll: vi.fn(),
      } as unknown as PipelineCheckpointRepo;

      const tasksJson = JSON.stringify({
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'Task 1', passes: true, attempts: 1 },
          { id: 'T-002', order: 2, title: 'Task 2', passes: true, attempts: 1 },
        ],
      });
      (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

      mockDetectResult = FAKE_CHANGE;
      mockRalphExecuteResult = {
        completed: 2,
        failed: 0,
        total: 2,
        taskResults: [],
        success: true,
      };

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        checkpointRepo: mockCheckpointRepo,
      });

      const result = await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(result.success).toBe(true);
      expect(eventBus.emit).toHaveBeenCalledWith(
        'build_stage_completed',
        expect.objectContaining({ completed: 2, failed: 0, total: 2 }),
      );
      expect(eventBus.emit).not.toHaveBeenCalledWith(
        'build_stage_failed',
        expect.objectContaining({ reason: 'zero_work' }),
      );
    });

    it('should NOT emit zero_work when completed=0 with success=true and hadCheckpoint=true (defense-in-depth)', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      const mockCheckpointRepo = {
        get: vi.fn().mockReturnValue({
          issueNumber: 1,
          stage: 'build',
          completedSteps: ['T-001', 'T-002'],
          nextStep: null,
          updatedAt: '2024-01-01T00:00:00Z',
        }),
        upsert: vi.fn(),
        delete: vi.fn(),
        deleteAll: vi.fn(),
      } as unknown as PipelineCheckpointRepo;

      const tasksJson = JSON.stringify({
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'Task 1', passes: true, attempts: 1 },
          { id: 'T-002', order: 2, title: 'Task 2', passes: true, attempts: 1 },
        ],
      });
      (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

      mockDetectResult = FAKE_CHANGE;
      mockRalphExecuteResult = {
        completed: 0,
        failed: 0,
        total: 2,
        taskResults: [],
        success: true,
      };

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
        checkpointRepo: mockCheckpointRepo,
      });

      const result = await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(result.success).toBe(true);
      expect(eventBus.emit).not.toHaveBeenCalledWith(
        'build_stage_failed',
        expect.objectContaining({ reason: 'zero_work' }),
      );
    });

    it('should emit build_stage_failed SSE with reason zero_work on zero-work', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      const tasksJson = JSON.stringify({
        version: 1,
        tasks: [
          { id: 'T-001', order: 1, title: 'Task 1', passes: true, attempts: 1 },
        ],
      });
      (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

      mockDetectResult = FAKE_CHANGE;
      mockRalphExecuteResult = {
        completed: 0,
        failed: 0,
        total: 1,
        taskResults: [],
        success: true,
      };

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(eventBus.emit).toHaveBeenCalledWith(
        'build_stage_failed',
        expect.objectContaining({ reason: 'zero_work' }),
      );
    });
  });

  describe('workflow_log entries', () => {
    it('should write build_failed to workflow_log when no change found', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const workflowLogRepo = { insert: vi.fn() } as any;

      mockDetectResult = null;

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo } as any,
      );

      expect(workflowLogRepo.insert).toHaveBeenCalledWith(
        'issue-1',
        null,
        'build_failed',
        expect.objectContaining({ reason: 'no_change_found' }),
      );
    });

    it('should write build_started and build_completed to workflow_log on success', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const workflowLogRepo = { insert: vi.fn() } as any;

    const tasksJson = JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', order: 1, title: 'Task 1', passes: false, attempts: 0 },
      ],
    });
    (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

    mockDetectResult = FAKE_CHANGE;
    mockRalphExecuteResult = {
      completed: 1,
      failed: 0,
      total: 1,
      taskResults: [],
      success: true,
    };

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    await ctrl.runPipelineBuildStage(
      createMockIssue(Stage.Build),
      { cwd: '/tmp', workflowLogRepo } as any,
    );

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'issue-1',
      null,
      'build_started',
      expect.objectContaining({ tasksCount: 1 }),
    );

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'issue-1',
      null,
      'build_completed',
      expect.objectContaining({ completed: 1, total: 1 }),
    );
  });

  it('should write build_failed to workflow_log on zero-work detection', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const workflowLogRepo = { insert: vi.fn() } as any;

    const tasksJson = JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', order: 1, title: 'Task 1', passes: true, attempts: 1 },
        { id: 'T-002', order: 2, title: 'Task 2', passes: true, attempts: 1 },
      ],
    });
    (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

    mockDetectResult = FAKE_CHANGE;
    mockRalphExecuteResult = {
      completed: 0,
      failed: 0,
      total: 2,
      taskResults: [],
      success: true,
    };

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    await ctrl.runPipelineBuildStage(
      createMockIssue(Stage.Build),
      { cwd: '/tmp', workflowLogRepo } as any,
    );

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'issue-1',
      null,
      'build_failed',
      expect.objectContaining({ reason: 'zero_work', total: 2 }),
    );
  });

  it('should write build_failed to workflow_log when tasks fail', async () => {
    const { issueRepo, eventBus } = createMockRepos();
    const workflowLogRepo = { insert: vi.fn() } as any;

    const tasksJson = JSON.stringify({
      version: 1,
      tasks: [
        { id: 'T-001', order: 1, title: 'Task 1', passes: false, attempts: 0 },
        { id: 'T-002', order: 2, title: 'Task 2', passes: false, attempts: 0 },
      ],
    });
    (fs.readFileSync as ReturnType<typeof vi.fn>).mockReturnValue(tasksJson);

    mockDetectResult = FAKE_CHANGE;
    mockRalphExecuteResult = {
      completed: 1,
      failed: 1,
      total: 2,
      taskResults: [],
      success: false,
    };

    const ctrl = new WorkflowController({
      artifactManager: createMockArtifactManager(),
      worktreePath: '/tmp/worktree',
      issueRepo,
      eventBus,
      projectId: 'proj-1',
    });

    await ctrl.runPipelineBuildStage(
      createMockIssue(Stage.Build),
      { cwd: '/tmp', workflowLogRepo } as any,
    );

    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      'issue-1',
      null,
      'build_failed',
      expect.objectContaining({ reason: 'tasks_failed' }),
    );
    });
  });

  describe('Swallow catch logging', () => {
    it('should handle eventBus.emit failures gracefully', async () => {
      const { issueRepo } = createMockRepos();
      const throwingEmit = vi.fn().mockImplementation(() => {
        throw new Error('emit failed');
      });
      const mockEventBus = {
        on: vi.fn(),
        off: vi.fn(),
        emit: throwingEmit,
        removeAllListeners: vi.fn(),
        emitPersistent: vi.fn(),
      } as unknown as EventBus;

      mockDetectResult = null;

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus: mockEventBus,
        projectId: 'proj-1',
      });

      const result = await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo: { insert: vi.fn() } as any } as any,
      );

      expect(result.success).toBe(false);
      expect(throwingEmit).toHaveBeenCalled();
    });

    it('should handle workflowLogRepo.insert failures gracefully', async () => {
      const { issueRepo, eventBus } = createMockRepos();
      const throwingLogRepo = {
        insert: vi.fn().mockImplementation(() => {
          throw new Error('db write failed');
        }),
      } as any;

      mockDetectResult = null;

      const ctrl = new WorkflowController({
        artifactManager: createMockArtifactManager(),
        worktreePath: '/tmp/worktree',
        issueRepo,
        eventBus,
        projectId: 'proj-1',
      });

      const result = await ctrl.runPipelineBuildStage(
        createMockIssue(Stage.Build),
        { cwd: '/tmp', workflowLogRepo: throwingLogRepo } as any,
      );

      expect(result.success).toBe(false);
      expect(throwingLogRepo.insert).toHaveBeenCalled();
    });
  });
});

describe('Build Pipeline API Endpoints', () => {
  let db: DatabaseManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let savedApiKeys: Record<string, string | undefined> = {};

  beforeEach(() => {
    savedApiKeys = {};
    for (const key of Object.keys(process.env)) {
      if (key.endsWith('_API_KEY')) {
        savedApiKeys[key] = process.env[key];
        delete process.env[key];
      }
    }

    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    const configRepo = stateManager.getConfigRepo();
    const projectRepo = stateManager.getProjectRepo();
    const issueRepo = stateManager.getIssueRepo();
    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);
  });

  afterEach(() => {
    db.close();
    for (const [key, val] of Object.entries(savedApiKeys)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  describe('GET /api/issues/:number/build-status', () => {
    let server: http.Server;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);
      const workflowLogRepo = new WorkflowLogRepo(db);
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        undefined,
        agentRunner,
        workflowLogRepo,
      ));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);
    });

    it('should return 400 when no active project', async () => {
      projectService.clearCurrent();

      const response = await request(server).get('/api/issues/1/build-status');

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('No active project');
    });

    it('should return 404 for non-existent issue', async () => {
      const response = await request(server).get('/api/issues/999/build-status');

      expect(response.status).toBe(404);
      expect(response.body.error).toContain('not found');
    });

    it('should return build status with correct structure', async () => {
      await issueService.create({ projectId: projectService.getCurrentId()!, title: 'Test Issue' });

      const response = await request(server).get('/api/issues/1/build-status');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data).toHaveProperty('stage');
      expect(response.body.data).toHaveProperty('status');
      expect(response.body.data).toHaveProperty('progress');
      expect(response.body.data).toHaveProperty('tasks');
      expect(response.body.data.progress).toHaveProperty('completed');
      expect(response.body.data.progress).toHaveProperty('failed');
      expect(response.body.data.progress).toHaveProperty('total');
      expect(response.body.data.progress).toHaveProperty('currentTask');
    });

    it('should return status running when issue is in Build stage', async () => {
      await issueService.create({ projectId: projectService.getCurrentId()!, title: 'Test Issue' });
      issueService.transitionToStageByNumber(projectService.getCurrentId()!, 1, Stage.Build);

      const response = await request(server).get('/api/issues/1/build-status');

      expect(response.status).toBe(200);
      expect(response.body.data.status).toBe('running');
    });

    it('should return status completed when issue is Done', async () => {
      await issueService.create({ projectId: projectService.getCurrentId()!, title: 'Test Issue' });
      issueService.transitionToStageByNumber(projectService.getCurrentId()!, 1, Stage.Done);

      const response = await request(server).get('/api/issues/1/build-status');

      expect(response.status).toBe(200);
      expect(response.body.data.status).toBe('completed');
    });

    it('should return progress with zero counts when no change found', async () => {
      await issueService.create({ projectId: projectService.getCurrentId()!, title: 'Test Issue' });

      const response = await request(server).get('/api/issues/1/build-status');

      expect(response.status).toBe(200);
      expect(response.body.data.progress.completed).toBe(0);
      expect(response.body.data.progress.failed).toBe(0);
      expect(response.body.data.progress.total).toBe(0);
      expect(response.body.data.progress.currentTask).toBeNull();
    });
  });

  describe('GET /api/issues/:number/tasks', () => {
    let server: http.Server;

    beforeEach(async () => {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);
      const workflowLogRepo = new WorkflowLogRepo(db);
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
        undefined,
        undefined,
        undefined,
        agentRunner,
        workflowLogRepo,
      ));
      server = createTestServer(app);

      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectService.setCurrent(project);
    });

    it('should return 400 when no active project', async () => {
      projectService.clearCurrent();

      const response = await request(server).get('/api/issues/1/tasks');

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('No active project');
    });

    it('should return 404 for non-existent issue', async () => {
      const response = await request(server).get('/api/issues/999/tasks');

      expect(response.status).toBe(404);
    });

    it('should return empty tasks when no change found', async () => {
      await issueService.create({ projectId: projectService.getCurrentId()!, title: 'Test Issue' });

      const response = await request(server).get('/api/issues/1/tasks');

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);
      expect(response.body.data).toHaveProperty('version');
      expect(response.body.data.tasks).toEqual([]);
    });
  });
});

describe('EventBus emitPersistent', () => {
  it('should emit event AND write to workflow_log', () => {
    const bus = new EventBus();
    const workflowLogRepo = { insert: vi.fn() } as any;
    const received: any[] = [];
    bus.on('build_stage_started', (data) => received.push(data));

    bus.emitPersistent('build_stage_started', {
      issueId: '1',
      projectId: 'proj-1',
      stage: 'build',
      changePath: '/tmp/change',
      tasksCount: 5,
      timestamp: new Date().toISOString(),
    }, { issueId: '1', workflowLogRepo });

    expect(received).toHaveLength(1);
    expect(received[0].tasksCount).toBe(5);
    expect(workflowLogRepo.insert).toHaveBeenCalledWith(
      '1',
      null,
      'build_stage_started',
      expect.objectContaining({ tasksCount: 5 }),
    );
  });

  it('should not throw when eventBus emit fails', () => {
    const bus = new EventBus();
    const workflowLogRepo = { insert: vi.fn() } as any;

    const originalEmit = bus.emit.bind(bus);
    bus.emit = vi.fn().mockImplementation(() => {
      throw new Error('emit failed');
    }) as any;

    expect(() => {
      bus.emitPersistent('build_stage_started', {
        issueId: '1',
        projectId: 'proj-1',
        stage: 'build',
        changePath: '/tmp',
        tasksCount: 1,
        timestamp: new Date().toISOString(),
      }, { issueId: '1', workflowLogRepo });
    }).not.toThrow();

    bus.emit = originalEmit;
  });

  it('should not throw when workflow_log write fails', () => {
    const bus = new EventBus();
    const throwingLogRepo = {
      insert: vi.fn().mockImplementation(() => {
        throw new Error('db error');
      }),
    } as any;

    const received: any[] = [];
    bus.on('build_stage_completed', (data) => received.push(data));

    expect(() => {
      bus.emitPersistent('build_stage_completed', {
        issueId: '1',
        projectId: 'proj-1',
        completed: 3,
        failed: 0,
        total: 3,
        duration: 5000,
        timestamp: new Date().toISOString(),
      }, { issueId: '1', workflowLogRepo: throwingLogRepo });
    }).not.toThrow();

    expect(received).toHaveLength(1);
  });

  it('should work without workflowLogRepo', () => {
    const bus = new EventBus();
    const received: any[] = [];
    bus.on('build_stage_failed', (data) => received.push(data));

    expect(() => {
      bus.emitPersistent('build_stage_failed', {
        issueId: '1',
        projectId: 'proj-1',
        reason: 'test',
        details: {},
        timestamp: new Date().toISOString(),
      }, { issueId: '1' });
    }).not.toThrow();

    expect(received).toHaveLength(1);
  });
});
