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
    it('should restore pending gates for awaiting issues', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Awaiting Issue' });

      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Plan);
      issueRepo.setApprovalState(issue.id, {
        stage: Stage.Plan,
        status: 'awaiting',
        requestedAt: new Date().toISOString(),
      });

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

      const status = service.getStatus();
      expect(status.recoverableIssues).toHaveLength(1);

      service.recoverIssues();

      const recovered = issueRepo.findById(issue.id);
      expect(recovered?.status).toBe(IssueStatus.Active);
      expect(recovered?.stage).toBe(Stage.Plan);
      expect(recovered?.approvalState?.status).toBe('awaiting');
      expect(service.hasPendingGate(issue.number)).toBe(true);
    });

    it('should preserve stage when recovering crashed orphaned issues', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const issue = issueService.create({ projectId: project.id, title: 'Crashed Issue' });

      issueRepo.updateStatus(issue.id, IssueStatus.Active);
      issueRepo.updateStage(issue.id, Stage.Build);

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

      service.recoverIssues();

      const recovered = issueRepo.findById(issue.id);
      expect(recovered?.status).toBe(IssueStatus.Blocked);
      expect(recovered?.stage).toBe(Stage.Build);
      expect(recovered?.approvalState).toBeUndefined();
    });

    it('should handle mixed orphaned issues (awaiting and crashed)', () => {
      const project = projectRepo.create({ name: 'Test Project', path: '/test' });
      const awaitingIssue = issueService.create({ projectId: project.id, title: 'Awaiting' });
      const crashedIssue = issueService.create({ projectId: project.id, title: 'Crashed' });

      issueRepo.updateStatus(awaitingIssue.id, IssueStatus.Active);
      issueRepo.updateStage(awaitingIssue.id, Stage.Plan);
      issueRepo.setApprovalState(awaitingIssue.id, {
        stage: Stage.Plan,
        status: 'awaiting',
        requestedAt: new Date().toISOString(),
      });

      issueRepo.updateStatus(crashedIssue.id, IssueStatus.Active);
      issueRepo.updateStage(crashedIssue.id, Stage.Build);

      const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);
      service.recoverIssues();

      const recoveredAwaiting = issueRepo.findById(awaitingIssue.id);
      expect(recoveredAwaiting?.status).toBe(IssueStatus.Active);
      expect(recoveredAwaiting?.stage).toBe(Stage.Plan);
      expect(service.hasPendingGate(awaitingIssue.number)).toBe(true);

      const recoveredCrashed = issueRepo.findById(crashedIssue.id);
      expect(recoveredCrashed?.status).toBe(IssueStatus.Blocked);
      expect(recoveredCrashed?.stage).toBe(Stage.Build);

      const statusAfter = service.getStatus();
      expect(statusAfter.recoverableIssues).toHaveLength(0);
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
