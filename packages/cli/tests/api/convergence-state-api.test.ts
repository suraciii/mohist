import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../../src/db/database';
import { ProjectService } from '../../src/services/project-service';
import { IssueService } from '../../src/services/issue-service';
import { StateManager } from '../../src/server/state-manager';
import { StageStateService } from '../../src/services/stage-state-service';
import { createIssueRoutes } from '../../src/api/issues';
import { Stage } from '../../src/types';

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

describe('Convergence State API', () => {
  let db: DatabaseManager;
  let stateManager: StateManager;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stageStateService: StageStateService;
  let server: http.Server;
  let savedApiKeys: Record<string, string | undefined> = {};
  let tempDirs: string[] = [];

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
    stageStateService = new StageStateService(db);
  });

  afterEach(() => {
    server?.close();
    db.close();
    for (const dir of tempDirs) {
      fs.rmSync(dir, { recursive: true, force: true });
    }
    tempDirs = [];
    for (const [key, val] of Object.entries(savedApiKeys)) {
      if (val === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = val;
      }
    }
  });

  function makeProjectPath(): string {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-convergence-'));
    tempDirs.push(dir);
    return dir;
  }

function createApp(): http.Server {
    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      undefined,
      stageStateService,
      undefined,
      undefined,
    ));
    return createTestServer(app);
  }

  describe('GET /api/issues/:number/stage-state convergence', () => {
    it('includes convergence state when check stage has a failed check with blocking items', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      stageStateService.ensureStage(issue.id, Stage.Check);
      stageStateService.upsertCheck(issue.id, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: 'Review found blockers',
        output: {
          verdict: 'FAIL',
          structuredResult: {
            verdict: 'FAIL',
            marker: '<promise>FAIL</promise>',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Missing error handling', suggestedAction: 'Add try-catch' },
              { id: 'item-2', severity: 'blocking', evidence: 'Unsafe type cast', suggestedAction: 'Add type guard' },
              { id: 'item-3', severity: 'follow-up', evidence: 'Consider refactoring', status: 'open' },
            ],
          },
        },
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

      expect(response.status).toBe(200);
      const checkStage = response.body.data.stages.find((s: any) => s.stage === 'check');
      expect(checkStage).toBeDefined();
      expect(checkStage.convergence).toBeDefined();

      const convergence = checkStage.convergence;
      expect(convergence.failedCheck).toBe('review-passed');
      expect(convergence.blockingItemCount).toBe(2);
      expect(convergence.directlyRepairedCount).toBe(0);
      expect(convergence.reactionAttempts).toBe(0);
      expect(convergence.attemptedItemIds).toEqual([]);
      expect(convergence.resolvedItemIds).toEqual([]);
      expect(convergence.unresolvedItemIds).toEqual([]);
      expect(convergence.newBlockingItemIds).toEqual([]);
      expect(convergence.nonBlockingItemIds).toEqual(['item-3']);
      expect(convergence.blockedReason).toBeTruthy();
    });

    it('tracks directly repaired items from review task output', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      stageStateService.ensureStage(issue.id, Stage.Check);
      stageStateService.upsertTask(issue.id, Stage.Check, {
        taskId: 'ai-review',
        title: 'AI Review',
        status: 'completed',
        output: {
          structuredResult: {
            verdict: 'FAIL',
            marker: '<promise>FAIL</promise>',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'Bug found', status: 'resolved' },
              { id: 'item-2', severity: 'blocking', evidence: 'Still broken' },
            ],
            repairedItemIds: ['item-1'],
            summary: 'Fixed item-1, item-2 still blocking',
          },
        },
      });
      stageStateService.upsertCheck(issue.id, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: '1 blocker remaining',
        output: {
          structuredResult: {
            verdict: 'FAIL',
            marker: '<promise>FAIL</promise>',
            items: [
              { id: 'item-2', severity: 'blocking', evidence: 'Still broken' },
            ],
          },
        },
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

      expect(response.status).toBe(200);
      const checkStage = response.body.data.stages.find((s: any) => s.stage === 'check');
      expect(checkStage.convergence).toBeDefined();

      const convergence = checkStage.convergence;
      expect(convergence.directlyRepairedCount).toBe(1);
      expect(convergence.blockingItemCount).toBe(1);
      expect(convergence.failedCheck).toBe('review-passed');
    });

    it('tracks reaction attempts with resolved and unresolved item IDs', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      stageStateService.ensureStage(issue.id, Stage.Check);
      stageStateService.upsertCheck(issue.id, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: '2 blockers remain',
        output: {
          structuredResult: {
            verdict: 'FAIL',
            marker: '<promise>FAIL</promise>',
            items: [
              { id: 'item-1', severity: 'blocking', evidence: 'First issue' },
              { id: 'item-2', severity: 'blocking', evidence: 'Second issue' },
              { id: 'item-3', severity: 'blocking', evidence: 'Third issue' },
            ],
          },
        },
      });
      stageStateService.upsertTask(issue.id, Stage.Check, {
        taskId: 'fix-review-findings',
        title: 'Fix review findings',
        status: 'completed',
        source: 'dynamic',
        output: {
          verdict: 'PASS',
          marker: '<promise>PASS</promise>',
          attemptedItemIds: ['item-1', 'item-2', 'item-3'],
          resolvedItemIds: ['item-1', 'item-2'],
          unresolvedItemIds: ['item-3'],
          items: [
            { id: 'item-1', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
            { id: 'item-2', severity: 'blocking', evidence: 'Fixed', status: 'resolved' },
            { id: 'item-3', severity: 'blocking', evidence: 'Still broken', status: 'unresolved' },
          ],
        },
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

      expect(response.status).toBe(200);
      const checkStage = response.body.data.stages.find((s: any) => s.stage === 'check');
      expect(checkStage.convergence).toBeDefined();

      const convergence = checkStage.convergence;
      expect(convergence.reactionAttempts).toBe(1);
      expect(convergence.attemptedItemIds).toEqual(['item-1', 'item-2', 'item-3']);
      expect(convergence.resolvedItemIds).toEqual(['item-1', 'item-2']);
      expect(convergence.unresolvedItemIds).toEqual(['item-3']);
    });

    it('separates non-blocking follow-up items from blocking items', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      stageStateService.ensureStage(issue.id, Stage.Check);
      stageStateService.upsertCheck(issue.id, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: '1 blocker, 2 follow-ups',
        output: {
          structuredResult: {
            verdict: 'FAIL',
            marker: '<promise>FAIL</promise>',
            items: [
              { id: 'blk-1', severity: 'blocking', evidence: 'Critical bug' },
              { id: 'fol-1', severity: 'follow-up', evidence: 'Refactor suggestion', status: 'open' },
              { id: 'fol-2', severity: 'warning', evidence: 'Minor improvement', status: 'open' },
            ],
          },
        },
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

      expect(response.status).toBe(200);
      const convergence = response.body.data.stages.find((s: any) => s.stage === 'check').convergence;

      expect(convergence.blockingItemCount).toBe(1);
      expect(convergence.nonBlockingItemIds).toEqual(['fol-1', 'fol-2']);
      expect(convergence.blockedReason).toBeTruthy();
    });

    it('omits convergence when no structured failure exists', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      stageStateService.ensureStage(issue.id, Stage.Plan);
      stageStateService.upsertTask(issue.id, Stage.Plan, {
        taskId: 'proposal',
        title: 'Write proposal',
        status: 'completed',
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

      expect(response.status).toBe(200);
      const planStage = response.body.data.stages.find((s: any) => s.stage === 'plan');
      expect(planStage.convergence).toBeUndefined();
    });

    it('preserves existing fields when convergence is absent', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      stageStateService.ensureStage(issue.id, Stage.Plan);
      stageStateService.upsertTask(issue.id, Stage.Plan, {
        taskId: 'proposal',
        title: 'Write proposal',
        status: 'completed',
        source: 'dynamic',
        order: 1,
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

      expect(response.status).toBe(200);
      const planStage = response.body.data.stages[0];
      expect(planStage.stage).toBe('plan');
      expect(planStage.status).toBe('running');
      expect(planStage.tasks).toBeDefined();
      expect(planStage.checks).toBeDefined();
      expect(planStage.approval).toBeNull();
    });

    it('computes newBlockingItemIds for unattempted blocking items', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      stageStateService.ensureStage(issue.id, Stage.Check);
      stageStateService.upsertCheck(issue.id, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: 'Blockers found',
        output: {
          structuredResult: {
            verdict: 'FAIL',
            marker: '<promise>FAIL</promise>',
            items: [
              { id: 'item-a', severity: 'blocking', evidence: 'Bug A' },
              { id: 'item-b', severity: 'blocking', evidence: 'Bug B' },
            ],
          },
        },
      });
      stageStateService.upsertTask(issue.id, Stage.Check, {
        taskId: 'fix-review-findings',
        title: 'Fix review findings',
        status: 'completed',
        source: 'dynamic',
        output: {
          kind: 'agent-session-task',
          result: {
            attemptedItemIds: ['item-a'],
            resolvedItemIds: ['item-a'],
            unresolvedItemIds: [],
            structuredOutput: 'Attempted Item IDs: item-a\nResolved Item IDs: item-a\nUnresolved Item IDs:',
          },
        },
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}/stage-state`);

      expect(response.status).toBe(200);
      const convergence = response.body.data.stages.find((s: any) => s.stage === 'check').convergence;

      expect(convergence.resolvedItemIds).toEqual(['item-a']);
      expect(convergence.newBlockingItemIds).toEqual(['item-b']);
    });
  });

  describe('GET /api/issues/:number convergence', () => {
    it('exposes convergence state on issue detail for current stage', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      db.run('UPDATE issues SET stage = ? WHERE id = ?', [Stage.Check, issue.id]);

      stageStateService.ensureStage(issue.id, Stage.Check);
      stageStateService.upsertCheck(issue.id, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: 'Review failed',
        output: {
          structuredResult: {
            verdict: 'FAIL',
            marker: '<promise>FAIL</promise>',
            items: [
              { id: 'i-1', severity: 'blocking', evidence: 'Missing test' },
            ],
          },
        },
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}`);

      expect(response.status).toBe(200);
      expect(response.body.success).toBe(true);

      const convergence = response.body.data.convergence;
      expect(convergence).toBeDefined();
      expect(convergence.failedCheck).toBe('review-passed');
      expect(convergence.blockingItemCount).toBe(1);
      expect(convergence.nonBlockingItemIds).toEqual([]);
    });

    it('exposes convergence from another stage when current stage lacks convergence', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      db.run('UPDATE issues SET stage = ? WHERE id = ?', [Stage.Integrate, issue.id]);

      stageStateService.ensureStage(issue.id, Stage.Check);
      stageStateService.upsertCheck(issue.id, Stage.Check, {
        checkName: 'review-passed',
        status: 'failed',
        message: 'Review failed',
        output: {
          structuredResult: {
            verdict: 'FAIL',
            marker: '<promise>FAIL</promise>',
            items: [
              { id: 'x-1', severity: 'blocking', evidence: 'Blocking issue' },
            ],
          },
        },
      });
      stageStateService.ensureStage(issue.id, Stage.Integrate);

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}`);

      expect(response.status).toBe(200);
      const convergence = response.body.data.convergence;
      expect(convergence).toBeDefined();
      expect(convergence.failedCheck).toBe('review-passed');
      expect(convergence.blockingItemCount).toBe(1);
    });

    it('omits convergence when no structured failure exists on issue detail', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      stageStateService.ensureStage(issue.id, Stage.Plan);
      stageStateService.upsertTask(issue.id, Stage.Plan, {
        taskId: 'proposal',
        title: 'Write proposal',
        status: 'completed',
      });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}`);

      expect(response.status).toBe(200);
      expect(response.body.data.convergence).toBeUndefined();
    });

    it('preserves existing issue detail fields alongside convergence', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      server = createApp();
      const response = await request(server).get(`/api/issues/${issue.number}`);

      expect(response.status).toBe(200);
      const data = response.body.data;
      expect(data.id).toBe(issue.id);
      expect(data.number).toBe(issue.number);
      expect(data.title).toBe('Test Issue');
      expect(data.projectName).toBeDefined();
      expect(data.baseBranch).toBeDefined();
      expect(Array.isArray(data.comments)).toBe(true);
    });

    it('excludes convergence when StageStateService is not configured', async () => {
      const project = await projectService.create({ name: 'Test', path: makeProjectPath() });
      projectService.setCurrent(project);
      const issue = await issueService.create({ projectId: project.id, title: 'Test Issue' });

      const app = new Hono();
      app.route('/api/issues', createIssueRoutes(
        issueService,
        projectService,
        stateManager,
      ));
      server = createTestServer(app);

      const response = await request(server).get(`/api/issues/${issue.number}`);

      expect(response.status).toBe(200);
      expect(response.body.data.convergence).toBeUndefined();
    });
  });
});
