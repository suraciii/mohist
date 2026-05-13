import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { Command } from 'commander';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
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
import { EventBus, AgentRunnerService } from '../src/services';
import { StateManager } from '../src/server/state-manager';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { createIssueRoutes } from '../src/api/issues';
import { setupIssueCommands, ingestBody } from '../src/cli/commands/issue';
import { apiClient } from '../src/cli/api-client';

vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));
vi.mock('../src/cli/api-client');

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

describe('Issue Create with Model - CLI Regression', () => {
  const tmpDir = os.tmpdir();

  function tmpFile(name: string, content: string): string {
    const p = path.join(tmpDir, `mohist-model-test-${name}`);
    fs.writeFileSync(p, content, 'utf-8');
    return p;
  }

  afterEach(() => {
    for (const f of fs.readdirSync(tmpDir).filter(f => f.startsWith('mohist-model-test-'))) {
      try { fs.unlinkSync(path.join(tmpDir, f)); } catch {}
    }
  });

  describe('ingestBody helper for use in CLI create tests', () => {
    it('resolves @file path correctly', async () => {
      const filePath = tmpFile('body.md', 'File body content');
      const result = await ingestBody(`@${filePath}`, undefined);
      expect(result.error).toBeNull();
      expect(result.body).toBe('File body content');
    });

    it('resolves plain body correctly', async () => {
      const result = await ingestBody('plain body', undefined);
      expect(result.error).toBeNull();
      expect(result.body).toBe('plain body');
    });

    it('returns promise for stdin (-)', async () => {
      const result = ingestBody('-', undefined);
      expect(typeof (result as any).then).toBe('function');
    });
  });

  describe('CLI issue create --model option', () => {
    let db: DatabaseManager;
    let projectService: ProjectService;
    let issueService: IssueService;
    let stateManager: StateManager;
    let projectId: string;

    beforeEach(async () => {
      db = new DatabaseManager({ inMemory: true });
      stateManager = new StateManager(db);

      const projectRepo = stateManager.getProjectRepo();
      const issueRepo = stateManager.getIssueRepo();
      const configRepo = stateManager.getConfigRepo();
      const commentRepo = stateManager.getCommentRepo();
      const labelRepo = stateManager.getLabelRepo();

      projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
      issueService = new IssueService(issueRepo, commentRepo);

      const project = await projectService.create({ name: 'Test Project', path: '/test/path' });
      projectId = project.id;
      projectService.setCurrent(project);
    });

    afterEach(() => {
      db.close();
    });

    function setupServer(): http.Server {
      const app = new Hono();
      const eventBus = new EventBus();
      const agentRunner = new AgentRunnerService(eventBus);
      app.route('/api/issues', createIssueRoutes(issueService, projectService, stateManager, undefined, undefined, undefined, agentRunner));
      const srv = createTestServer(app);
      srv.listen(0);
      return srv;
    }

    it('sends model in create request when --model is provided', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: {
          id: 'issue-1',
          number: 1,
          title: 'Test Issue',
          priority: 'p2',
          stage: 'backlog',
          status: 'active',
          projectId,
          model: 'anthropic/claude-sonnet',
          labels: [],
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
        },
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(((code: number) => { throw new Error(`process.exit(${code})`); }) as typeof process.exit);
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'create', 'Test Issue', '--model', 'anthropic/claude-sonnet']);

      expect(mockedApiClient).toHaveBeenCalledWith(
        'POST',
        '/issues',
        expect.objectContaining({
          title: 'Test Issue',
          model: 'anthropic/claude-sonnet',
        }),
      );
      expect(errorSpy).not.toHaveBeenCalled();
      expect(exitSpy).not.toHaveBeenCalledWith(1);
    });

    it('sends model combined with plain body', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: {
          id: 'issue-1',
          number: 1,
          title: 'Body Model Issue',
          priority: 'p2',
          stage: 'backlog',
          status: 'active',
          projectId,
          model: 'openai/gpt-4o',
          labels: [],
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
        },
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(((code: number) => { throw new Error(`process.exit(${code})`); }) as typeof process.exit);
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'create', 'Body Model Issue', '--body', 'Some body text', '--model', 'openai/gpt-4o']);

      expect(mockedApiClient).toHaveBeenCalledWith(
        'POST',
        '/issues',
        expect.objectContaining({
          title: 'Body Model Issue',
          body: 'Some body text',
          model: 'openai/gpt-4o',
        }),
      );
      expect(errorSpy).not.toHaveBeenCalled();
      expect(exitSpy).not.toHaveBeenCalledWith(1);
    });

    it('sends model combined with @file body source', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      const filePath = tmpFile('issue-body.md', '# Issue Body\n\nSome detailed content');
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: {
          id: 'issue-1',
          number: 1,
          title: 'File Body Model Issue',
          priority: 'p2',
          stage: 'backlog',
          status: 'active',
          projectId,
          model: 'anthropic/claude-sonnet',
          labels: [],
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
        },
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(((code: number) => { throw new Error(`process.exit(${code})`); }) as typeof process.exit);
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'create', 'File Body Model Issue', '--body', `@${filePath}`, '--model', 'anthropic/claude-sonnet']);

      expect(mockedApiClient).toHaveBeenCalledWith(
        'POST',
        '/issues',
        expect.objectContaining({
          title: 'File Body Model Issue',
          body: '# Issue Body\n\nSome detailed content',
          model: 'anthropic/claude-sonnet',
        }),
      );
      expect(errorSpy).not.toHaveBeenCalled();
      expect(exitSpy).not.toHaveBeenCalledWith(1);
    });

    it('exits with code 1 when API rejects invalid model format', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: false,
        error: 'Invalid model format. Expected provider/model (e.g. anthropic/claude-sonnet)',
      });

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(((code: number) => { throw new Error(`process.exit(${code})`); }) as typeof process.exit);
      const program = new Command();
      setupIssueCommands(program);

      let exitThrown = false;
      try {
        await program.parseAsync(['node', 'test', 'issue', 'create', 'Bad Model Issue', '--model', 'invalid-model']);
      } catch (e: any) {
        if (e.message && e.message.startsWith('process.exit(')) {
          exitThrown = true;
        } else {
          throw e;
        }
      }
      expect(exitThrown).toBe(true);

      expect(mockedApiClient).toHaveBeenCalledWith(
        'POST',
        '/issues',
        expect.objectContaining({
          title: 'Bad Model Issue',
          model: 'invalid-model',
        }),
      );
      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('model'));
      expect(exitSpy).toHaveBeenCalledWith(1);
    });

    it('sends model combined with priority', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: {
          id: 'issue-1',
          number: 1,
          title: 'High Priority Model Issue',
          priority: 'p1',
          stage: 'backlog',
          status: 'active',
          projectId,
          model: 'anthropic/claude-sonnet',
          labels: [],
          createdAt: '2026-01-01T00:00:00Z',
          updatedAt: '2026-01-01T00:00:00Z',
        },
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(((code: number) => { throw new Error(`process.exit(${code})`); }) as typeof process.exit);
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'create', 'High Priority Model Issue', '-p', 'p1', '--model', 'anthropic/claude-sonnet']);

      expect(mockedApiClient).toHaveBeenCalledWith(
        'POST',
        '/issues',
        expect.objectContaining({
          title: 'High Priority Model Issue',
          priority: 'p1',
          model: 'anthropic/claude-sonnet',
        }),
      );
      expect(errorSpy).not.toHaveBeenCalled();
      expect(exitSpy).not.toHaveBeenCalledWith(1);
    });
  });
});