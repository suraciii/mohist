import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { EventBus } from '../src/services/event-bus';
import { IssueService } from '../src/services/issue-service';
import { Stage, IssueStatus } from '../src/types';

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

  describe('startPipeline', () => {
    it('should return { started: false, error: ... } when issue has pending approval', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue' });

      issueRepo.setApprovalState(issue.id, {
        stage: Stage.Plan,
        status: 'awaiting',
        requestedAt: new Date().toISOString(),
      });

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

      const result = service.startPipeline(
        issue,
        project.id,
        issueRepo,
        '/test',
        { cwd: '/test' },
      );

      expect(result.started).toBe(false);
      expect(result.error).toMatch(/pending approval|approval/);
    });

    it('should proceed normally when no pending approval', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Test Issue 2' });

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

      const result = service.startPipeline(
        issue,
        project.id,
        issueRepo,
        '/test',
        { cwd: '/test' },
      );

      expect(result.started).toBe(true);
      expect(result.error).toBeUndefined();
    });
  });

  describe('hasPendingGate', () => {
    it('should return false when no gates pending', () => {
      const service = new AgentRunnerService(eventBus, undefined, undefined, 8);
      expect(service.hasPendingGate(1)).toBe(false);
    });
  });

  describe('recoverIssues', () => {
    it('should recover orphaned active issues to blocked/draft', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Orphaned Issue' });
      
      // Set issue to active + plan stage (orphaned state)
      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.setApprovalState(issue.id, {
        stage: Stage.Plan,
        status: 'awaiting',
        requestedAt: new Date().toISOString(),
      });

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      
      // Verify issue is detected as recoverable
      const status = service.getStatus();
      expect(status.recoverableIssues).toHaveLength(1);
      expect(status.recoverableIssues[0].issueNumber).toBe(issue.number);

      // Recover the issue
      service.recoverIssues();

      // Verify issue is recovered
      const recovered = issueRepo.findById(issue.id);
      expect(recovered?.status).toBe(IssueStatus.Blocked);
      expect(recovered?.stage).toBe(Stage.Draft);
      expect(recovered?.approvalState).toBeUndefined();

      // Verify recoverableIssues is cleared
      const statusAfter = service.getStatus();
      expect(statusAfter.recoverableIssues).toHaveLength(0);
    });

    it('should handle multiple orphaned issues', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue1 = issueService.create({ projectId: project.id, title: 'Orphaned 1' });
      const issue2 = issueService.create({ projectId: project.id, title: 'Orphaned 2' });
      
      issueRepo.updateStatus(issue1.id, IssueStatus.Active);
      issueRepo.updateStage(issue1.id, Stage.Plan);
      issueRepo.updateStatus(issue2.id, IssueStatus.Active);
      issueRepo.updateStage(issue2.id, Stage.Build);

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      service.recoverIssues();

      const recovered1 = issueRepo.findById(issue1.id);
      const recovered2 = issueRepo.findById(issue2.id);
      
      expect(recovered1?.status).toBe(IssueStatus.Blocked);
      expect(recovered1?.stage).toBe(Stage.Draft);
      expect(recovered2?.status).toBe(IssueStatus.Blocked);
      expect(recovered2?.stage).toBe(Stage.Draft);
    });

    it('should not affect draft issues', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Draft Issue' });
      
      // Issue starts as draft by default
      expect(issue.status).toBe(IssueStatus.Active);
      expect(issue.stage).toBe(Stage.Draft);

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      service.recoverIssues();

      const unchanged = issueRepo.findById(issue.id);
      expect(unchanged?.status).toBe(IssueStatus.Active);
      expect(unchanged?.stage).toBe(Stage.Draft);
    });

    it('should not affect non-active issues', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Blocked Issue' });
      
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
      issueRepo.updateStage(issue.id, Stage.Plan);

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      service.recoverIssues();

      const unchanged = issueRepo.findById(issue.id);
      expect(unchanged?.status).toBe(IssueStatus.Blocked);
      expect(unchanged?.stage).toBe(Stage.Plan);
    });

    it('should handle missing issueRepo gracefully', () => {
      const service = new AgentRunnerService(eventBus, undefined, undefined, 8);
      
      // Should not throw
      expect(() => service.recoverIssues()).not.toThrow();
    });
  });
});
