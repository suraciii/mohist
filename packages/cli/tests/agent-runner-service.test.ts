import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { EventBus } from '../src/services/event-bus';
import { IssueService } from '../src/services/issue-service';
import { Stage } from '../src/types';

describe('AgentRunnerService', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
  });

  afterEach(() => {
    db.close();
  });

  describe('start', () => {
    it('should return { started: false, error: ... } when issue has pending approval', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      issueRepo.setApprovalState(issue.id, {
        status: 'awaiting',
        requestedAt: new Date(),
        requestedBy: 'user',
      });

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      const mockSessionManager = {
        create: vi.fn(),
        appendMessage: vi.fn(),
        pause: vi.fn(),
        resume: vi.fn(),
        close: vi.fn(),
      } as any;

      const result = service.start(
        issue,
        project.id,
        issueRepo,
        {} as any,
        undefined,
        '/test',
        mockSessionManager,
      );

      expect(result.started).toBe(false);
      expect(result.error).toContain('pending approval');
      expect(result.error).toMatch(/resume|submit_approval/);
    });

    it('should proceed normally when no pending approval', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue 2' });

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      const mockSessionManager = {
        create: vi.fn().mockReturnValue({ id: 'session-1' }),
        appendMessage: vi.fn(),
        pause: vi.fn(),
        resume: vi.fn(),
        close: vi.fn(),
      } as any;

      const result = service.start(
        issue,
        project.id,
        issueRepo,
        {} as any,
        undefined,
        '/test',
        mockSessionManager,
      );

      expect(result.started).toBe(true);
      expect(result.error).toBeUndefined();
    });
  });
});
