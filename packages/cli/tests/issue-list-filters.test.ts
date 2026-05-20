import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import http from 'node:http';
import { Hono } from 'hono';
import request from 'supertest';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { StateManager } from '../src/server/state-manager';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { createIssueRoutes } from '../src/api/issues';
import { Stage, IssueStatus, MergeState } from '../src/types';

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

describe('GET /api/issues scope filtering', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let projectService: ProjectService;
  let issueService: IssueService;
  let stateManager: StateManager;
  let projectId: string;
  let server: http.Server;

  beforeEach(async () => {
    db = new DatabaseManager({ inMemory: true });
    stateManager = new StateManager(db);
    projectRepo = stateManager.getProjectRepo();
    issueRepo = stateManager.getIssueRepo();

    const commentRepo = stateManager.getCommentRepo();
    const labelRepo = stateManager.getLabelRepo();
    projectService = new ProjectService(projectRepo, new ConfigRepo(db), issueRepo, labelRepo);
    issueService = new IssueService(issueRepo, commentRepo);

    const createdProject = await projectService.create({ name: 'TestProject', path: '/test/path' });
    projectService.setCurrent(createdProject);
    projectId = createdProject.id;

    const app = new Hono();
    app.route('/api/issues', createIssueRoutes(
      issueService,
      projectService,
      stateManager,
    ));
    server = createTestServer(app);
  });

  afterEach(() => {
    server.close();
    db.close();
  });

  function createIssue(overrides: Partial<{ stage: Stage; status: IssueStatus; approvalState?: { stage: Stage; status: string }; mergeState?: MergeState }> = {}) {
    const issue = issueService.create({
      projectId,
      title: `Issue ${Date.now()}`,
      priority: 'p2',
    });
    if (overrides.stage) {
      issueService.transitionToStage(issue.id, overrides.stage);
    }
    if (overrides.status) {
      issueService.setStatus(issue.id, overrides.status);
    }
    if (overrides.approvalState) {
      issueRepo.setApprovalState(issue.id, overrides.approvalState as any);
    }
    if (overrides.mergeState) {
      issueRepo.setMergeState(issue.id, overrides.mergeState);
    }
    return issue;
  }

  describe('stage=active alias', () => {
    it('returns plan/build/check/integrate issues that are not closed or completed', async () => {
      const plan = createIssue({ stage: Stage.Plan, status: IssueStatus.Active });
      createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      createIssue({ stage: Stage.Check, status: IssueStatus.Active });
      createIssue({ stage: Stage.Integrate, status: IssueStatus.Active });
      createIssue({ stage: Stage.Done, status: IssueStatus.Completed });

      const response = await request(server).get('/api/issues?stage=active');

      expect(response.status).toBe(200);
      const numbers = response.body.data.map((i: any) => i.number);
      expect(numbers).toContain(plan.number);
      expect(numbers).toHaveLength(4);
    });

    it('does NOT return backlog active issues', async () => {
      createIssue({ stage: Stage.Backlog, status: IssueStatus.Active });

      const response = await request(server).get('/api/issues?stage=active');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(0);
    });

    it('does NOT return closed or completed issues', async () => {
      createIssue({ stage: Stage.Plan, status: IssueStatus.Closed });
      createIssue({ stage: Stage.Build, status: IssueStatus.Completed });

      const response = await request(server).get('/api/issues?stage=active');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(0);
    });
  });

  describe('multi-stage filter (comma-separated)', () => {
    it('returns issues in build OR check stage', async () => {
      const buildIssue = createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      createIssue({ stage: Stage.Check, status: IssueStatus.Active });
      createIssue({ stage: Stage.Plan, status: IssueStatus.Active });

      const response = await request(server).get('/api/issues?stage=build,check');

      expect(response.status).toBe(200);
      const numbers = response.body.data.map((i: any) => i.number);
      expect(numbers).toContain(buildIssue.number);
      expect(numbers).toHaveLength(2);
    });

    it('accepts stage names case-insensitively', async () => {
      createIssue({ stage: Stage.Build, status: IssueStatus.Active });

      const response = await request(server).get('/api/issues?stage=BUILD');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
    });

    it('returns HTTP 400 for unknown stage name', async () => {
      const response = await request(server).get('/api/issues?stage=unknown');

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Unknown stage or alias');
      expect(response.body.error).toContain('unknown');
    });

    it('returns HTTP 400 for unknown alias', async () => {
      const response = await request(server).get('/api/issues?stage=notreal');

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Unknown stage or alias');
      expect(response.body.error).toContain('notreal');
    });

    it('returns HTTP 400 for mixed valid stage and unknown alias', async () => {
      const response = await request(server).get('/api/issues?stage=build,unknown');

      expect(response.status).toBe(400);
      expect(response.body.error).toContain('Unknown stage or alias');
    });
  });

  describe('stage selection composition with priority, label, archived, all', () => {
    it('stage filter composes with priority using AND', async () => {
      createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      const p1Issue = createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      issueService.update(p1Issue.id, { priority: 'p1' as any });

      const response = await request(server).get('/api/issues?stage=build&priority=p1');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].priority).toBe('p1');
    });

    it('stage filter composes with label using AND', async () => {
      createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      const labeledIssue = createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      issueService.update(labeledIssue.id, { labels: ['frontend'] } as any);

      const response = await request(server).get('/api/issues?stage=build&label=frontend');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].labels).toContain('frontend');
    });

    it('stage filter composes with archived=true using AND', async () => {
      const issue = createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      issueService.archive(projectId, issue.number);
      const archivedPlan = createIssue({ stage: Stage.Plan, status: IssueStatus.Active });
      issueService.archive(projectId, archivedPlan.number);

      const response = await request(server).get('/api/issues?stage=build&archived=true');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].number).toBe(issue.number);
    });

    it('stage filter composes with all=true using AND', async () => {
      const issue = createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      issueService.archive(projectId, issue.number);
      createIssue({ stage: Stage.Plan, status: IssueStatus.Active });

      const response = await request(server).get('/api/issues?stage=build&all=true');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data.some((i: any) => i.number === issue.number)).toBe(true);
    });
  });

  describe('attention=true filter', () => {
    it('returns issues awaiting approval', async () => {
      const issue = createIssue({ stage: Stage.Plan, status: IssueStatus.Paused });
      issueRepo.setApprovalState(issue.id, { stage: Stage.Plan, status: 'awaiting' as any, output: null, requestedAt: new Date().toISOString() });

      const response = await request(server).get('/api/issues?attention=true');

      expect(response.status).toBe(200);
      expect(response.body.data.some((i: any) => i.number === issue.number)).toBe(true);
    });

    it('returns blocked issues', async () => {
      const issue = createIssue({ stage: Stage.Build, status: IssueStatus.Blocked });

      const response = await request(server).get('/api/issues?attention=true');

      expect(response.status).toBe(200);
      expect(response.body.data.some((i: any) => i.number === issue.number)).toBe(true);
    });

    it('returns interrupted issues', async () => {
      const issue = createIssue({ stage: Stage.Build, status: IssueStatus.Interrupted });

      const response = await request(server).get('/api/issues?attention=true');

      expect(response.status).toBe(200);
      expect(response.body.data.some((i: any) => i.number === issue.number)).toBe(true);
    });

    it('does not return completed issues without successful merge evidence as attention items', async () => {
      const issue = createIssue({ stage: Stage.Done, status: IssueStatus.Completed });
      issueRepo.setMergeState(issue.id, MergeState.Pending);

      const response = await request(server).get('/api/issues?attention=true');

      expect(response.status).toBe(200);
      expect(response.body.data.some((i: any) => i.number === issue.number)).toBe(false);
    });

    it('returns merge conflict delivery blockers', async () => {
      const issue = createIssue({ stage: Stage.Integrate, status: IssueStatus.Active, mergeState: MergeState.Conflict });

      const response = await request(server).get('/api/issues?attention=true');

      expect(response.status).toBe(200);
      expect(response.body.data.some((i: any) => i.number === issue.number)).toBe(true);
    });

    it('does NOT include normal running/probing issues', async () => {
      createIssue({ stage: Stage.Plan, status: IssueStatus.Active });
      createIssue({ stage: Stage.Build, status: IssueStatus.Active });
      createIssue({ stage: Stage.Check, status: IssueStatus.Active });

      const response = await request(server).get('/api/issues?attention=true');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(0);
    });

    it('composes with stage filter using AND', async () => {
      const buildBlocked = createIssue({ stage: Stage.Build, status: IssueStatus.Blocked });
      createIssue({ stage: Stage.Plan, status: IssueStatus.Blocked });

      const response = await request(server).get('/api/issues?attention=true&stage=build');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].number).toBe(buildBlocked.number);
    });

    it('composes with priority filter using AND', async () => {
      const p0Blocked = createIssue({ stage: Stage.Build, status: IssueStatus.Blocked });
      issueService.update(p0Blocked.id, { priority: 'p0' as any });
      createIssue({ stage: Stage.Build, status: IssueStatus.Blocked });

      const response = await request(server).get('/api/issues?attention=true&priority=p0');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].priority).toBe('p0');
    });

    it('composes with label filter using AND', async () => {
      const frontendBlocked = createIssue({ stage: Stage.Build, status: IssueStatus.Blocked });
      issueService.update(frontendBlocked.id, { labels: ['frontend'] } as any);
      createIssue({ stage: Stage.Build, status: IssueStatus.Blocked });

      const response = await request(server).get('/api/issues?attention=true&label=frontend');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(1);
      expect(response.body.data[0].labels).toContain('frontend');
    });

    it('returns empty list when no attention issues exist', async () => {
      createIssue({ stage: Stage.Plan, status: IssueStatus.Active });

      const response = await request(server).get('/api/issues?attention=true');

      expect(response.status).toBe(200);
      expect(response.body.data).toHaveLength(0);
    });
  });
});
