import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { EpicRepo } from '../src/db/epic-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { LabelRepo } from '../src/db/label-repo';
import { EpicService, CrossProjectEpicMembershipError, DuplicateEpicMembershipError } from '../src/services/epic-service';
import { IssueService } from '../src/services/issue-service';
import { ProjectService } from '../src/services/project-service';
import { EpicStatus, IssueStatus, Stage } from '../src/types';
import { createEpicRoutes } from '../src/api/epics';
import { Hono } from 'hono';

describe('EpicService Regression', () => {
  let db: DatabaseManager;
  let epicRepo: EpicRepo;
  let issueRepo: IssueRepo;
  let projectRepo: ProjectRepo;
  let epicService: EpicService;
  let issueService: IssueService;
  let projectService: ProjectService;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);

    epicRepo = new EpicRepo(db);
    issueRepo = new IssueRepo(db);
    projectRepo = new ProjectRepo(db);

    const project = projectRepo.create({ name: 'Test Project', path: '/test/path' });
    projectId = project.id;
    projectService = new ProjectService(projectRepo, new ConfigRepo(db), issueRepo, new LabelRepo(db));
    projectService.setCurrent(project);
    issueService = new IssueService(issueRepo, projectRepo, {} as any, {} as any, {} as any);
    epicService = new EpicService(epicRepo, issueRepo);
  });

  afterEach(() => {
    db.close();
  });

  describe('create', () => {
    it('should create an Epic with active status', () => {
      const epic = epicService.create({ projectId, title: 'Test Epic',
        description: 'Test description',
        priority: 'p1',
      });

      expect(epic.id).toBeDefined();
      expect(epic.title).toBe('Test Epic');
      expect(epic.description).toBe('Test description');
      expect(epic.priority).toBe('p1');
      expect(epic.status).toBe(EpicStatus.Active);
      expect(epic.createdAt).toBeDefined();
      expect(epic.updatedAt).toBeDefined();
    });

    it('should throw on missing title', () => {
      expect(() => epicService.create({ projectId, title: '',
        description: 'Test',
        priority: 'p1',
      })).toThrow('title is required');
    });

    it('should throw on missing description', () => {
      expect(() => epicService.create({ projectId, title: 'Test',
        description: '',
        priority: 'p1',
      })).toThrow('description is required');
    });

    it('should throw on invalid priority', () => {
      expect(() => epicService.create({ projectId, title: 'Test',
        description: 'Test',
        priority: 'invalid' as any,
      })).toThrow('Invalid priority');
    });
  });

  describe('list with projected progress', () => {
    it('should list Epics with zero progress when no linked issues', () => {
      epicService.create({ projectId, title: 'Epic 1', description: 'Desc', priority: 'p1' });
      epicService.create({ projectId, title: 'Epic 2', description: 'Desc', priority: 'p2' });

      const epics = epicService.list(projectId);

      expect(epics).toHaveLength(2);
      expect(epics[0].progress.deliveredCount).toBe(0);
      expect(epics[0].progress.totalIssueCount).toBe(0);
      expect(epics[0].progress.nextIssue).toBeNull();
      expect(epics[0].progress.readyToMarkDone).toBe(false);
    });

    it('should only list Epics for the requested project', () => {
      const otherProject = projectRepo.create({ name: 'Other Project', path: '/other/path' });
      epicService.create({ projectId, title: 'Current Epic', description: 'Desc', priority: 'p1' });
      epicService.create({ projectId: otherProject.id, title: 'Other Epic', description: 'Desc', priority: 'p1' });

      const currentEpics = epicService.list(projectId);
      const otherEpics = epicService.list(otherProject.id);

      expect(currentEpics.map(e => e.title)).toEqual(['Current Epic']);
      expect(otherEpics.map(e => e.title)).toEqual(['Other Epic']);
    });

    it('should project delivered and total counts from linked issues', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue1 = issueService.create({ projectId, title: 'Issue 1' });
      issueService.setStatus(issue1.id, IssueStatus.Closed);

      const issue2 = issueService.create({ projectId, title: 'Issue 2' });
      issueService.setStatus(issue2.id, IssueStatus.Active);
      issueService.transitionToStage(issue2.id, Stage.Check);

      const issue3 = issueService.create({ projectId, title: 'Issue 3' });
      issueService.setStatus(issue3.id, IssueStatus.Completed);

      epicService.addIssue(projectId, epic.id, issue1.id);
      epicService.addIssue(projectId, epic.id, issue2.id);
      epicService.addIssue(projectId, epic.id, issue3.id);

      const result = epicService.getById(projectId, epic.id);

      expect(result?.progress.totalIssueCount).toBe(3);
      expect(result?.progress.deliveredCount).toBe(2);
    });

    it('should project progress in list responses from linked issues', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const blockedIssue = issueService.create({ projectId, title: 'Blocked Issue' });
      issueService.setStatus(blockedIssue.id, IssueStatus.Blocked);

      const doneIssue = issueService.create({ projectId, title: 'Done Issue' });
      issueService.setStatus(doneIssue.id, IssueStatus.Completed);

      epicService.addIssue(projectId, epic.id, blockedIssue.id);
      epicService.addIssue(projectId, epic.id, doneIssue.id);

      const listedEpic = epicService.list(projectId)[0];

      expect(listedEpic.progress.totalIssueCount).toBe(2);
      expect(listedEpic.progress.deliveredCount).toBe(1);
      expect(listedEpic.progress.nextIssue?.id).toBe(blockedIssue.id);
      expect(listedEpic.progress.readyToMarkDone).toBe(false);
    });
  });

  describe('getById with detail', () => {
    it('should return Epic detail with linked issues', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Test Issue' });
      epicService.addIssue(projectId, epic.id, issue.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail).toBeDefined();
      expect(detail?.linkedIssues).toHaveLength(1);
      expect(detail?.linkedIssues[0].id).toBe(issue.id);
      expect(detail?.progress).toBeDefined();
    });

    it('should return null for non-existent Epic', () => {
      const result = epicService.getById(projectId, 'non-existent-id');
      expect(result).toBeNull();
    });

    it('should return null when an Epic belongs to another project', () => {
      const otherProject = projectRepo.create({ name: 'Other Project', path: '/other/path' });
      const otherEpic = epicService.create({ projectId: otherProject.id, title: 'Other Epic', description: 'Desc', priority: 'p1' });

      expect(epicService.getById(projectId, otherEpic.id)).toBeNull();
      expect(epicService.getById(otherProject.id, otherEpic.id)?.title).toBe('Other Epic');
    });
  });

  describe('duplicate primary Epic membership rejection', () => {
    it('should reject adding an issue that already belongs to another Epic', () => {
      const epic1 = epicService.create({ projectId, title: 'Epic 1', description: 'Desc', priority: 'p1' });
      const epic2 = epicService.create({ projectId, title: 'Epic 2', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Shared Issue' });
      epicService.addIssue(projectId, epic1.id, issue.id);

      expect(() => epicService.addIssue(projectId, epic2.id, issue.id))
        .toThrow(DuplicateEpicMembershipError);
    });

    it('should throw DuplicateEpicMembershipError with correct properties', () => {
      const epic1 = epicService.create({ projectId, title: 'Epic 1', description: 'Desc', priority: 'p1' });
      const epic2 = epicService.create({ projectId, title: 'Epic 2', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Shared Issue' });
      epicService.addIssue(projectId, epic1.id, issue.id);

      try {
        epicService.addIssue(projectId, epic2.id, issue.id);
        expect.fail('Should have thrown');
      } catch (error) {
        expect(error).toBeInstanceOf(DuplicateEpicMembershipError);
        expect((error as DuplicateEpicMembershipError).issueId).toBe(issue.id);
        expect((error as DuplicateEpicMembershipError).existingEpicId).toBe(epic1.id);
        expect((error as DuplicateEpicMembershipError).existingEpicTitle).toBe('Epic 1');
      }
    });

    it('should allow adding different issues to different Epics', () => {
      const epic1 = epicService.create({ projectId, title: 'Epic 1', description: 'Desc', priority: 'p1' });
      const epic2 = epicService.create({ projectId, title: 'Epic 2', description: 'Desc', priority: 'p1' });

      const issue1 = issueService.create({ projectId, title: 'Issue 1' });
      const issue2 = issueService.create({ projectId, title: 'Issue 2' });

      epicService.addIssue(projectId, epic1.id, issue1.id);
      epicService.addIssue(projectId, epic2.id, issue2.id);

      expect(epicService.getById(projectId, epic1.id)?.linkedIssues).toHaveLength(1);
      expect(epicService.getById(projectId, epic2.id)?.linkedIssues).toHaveLength(1);
    });

    it('should reject adding same issue to same Epic twice', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Issue' });
      epicService.addIssue(projectId, epic.id, issue.id);

      expect(() => epicService.addIssue(projectId, epic.id, issue.id))
        .toThrow(DuplicateEpicMembershipError);
    });

    it('should translate racy unique constraint failures into duplicate membership errors', () => {
      const existingEpic = epicService.create({ projectId, title: 'Existing Epic', description: 'Desc', priority: 'p1' });
      const targetEpic = epicService.create({ projectId, title: 'Target Epic', description: 'Desc', priority: 'p1' });
      const issue = issueService.create({ projectId, title: 'Racy Issue' });

      const findSpy = vi.spyOn(epicRepo, 'findEpicByIssueId');
      findSpy
        .mockReturnValueOnce(null)
        .mockReturnValueOnce(existingEpic);
      vi.spyOn(epicRepo, 'addIssue').mockImplementation(() => {
        throw new Error('UNIQUE constraint failed: epic_issues.issue_id');
      });

      expect(() => epicService.addIssue(projectId, targetEpic.id, issue.id))
        .toThrow(DuplicateEpicMembershipError);
    });

    it('should reject adding a non-existent issue id', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      expect(() => epicService.addIssue(projectId, epic.id, 'missing-issue-id')).toThrow('Issue not found');
    });

    it('should reject adding an issue from another project', () => {
      const otherProject = projectRepo.create({ name: 'Other Project', path: '/other/path' });
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });
      const otherIssue = issueService.create({ projectId: otherProject.id, title: 'Other Issue' });

      expect(() => epicService.addIssue(projectId, epic.id, otherIssue.id))
        .toThrow(CrossProjectEpicMembershipError);
      expect(epicService.getById(projectId, epic.id)?.linkedIssues).toHaveLength(0);
    });
  });

  describe('next issue ordering', () => {
    it('should prioritize blocked issue as next', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const activeIssue = issueService.create({ projectId, title: 'Active Issue' });
      issueService.setStatus(activeIssue.id, IssueStatus.Active);

      const blockedIssue = issueService.create({ projectId, title: 'Blocked Issue' });
      issueService.setStatus(blockedIssue.id, IssueStatus.Blocked);

      epicService.addIssue(projectId, epic.id, activeIssue.id);
      epicService.addIssue(projectId, epic.id, blockedIssue.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail?.progress.nextIssue?.id).toBe(blockedIssue.id);
    });

    it('should pick active issue when no blocked issues exist', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const backlogIssue = issueService.create({ projectId, title: 'Backlog Issue' });
      issueService.setStatus(backlogIssue.id, IssueStatus.Active);
      issueService.transitionToStage(backlogIssue.id, Stage.Backlog);

      const activeIssue = issueService.create({ projectId, title: 'Active Issue' });
      issueService.setStatus(activeIssue.id, IssueStatus.Active);
      issueService.transitionToStage(activeIssue.id, Stage.Check);

      epicService.addIssue(projectId, epic.id, backlogIssue.id);
      epicService.addIssue(projectId, epic.id, activeIssue.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail?.progress.nextIssue?.id).toBe(activeIssue.id);
    });

    it('should pick backlog issue when no blocked or active issues exist', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const backlogIssue = issueService.create({ projectId, title: 'Backlog Issue' });
      issueService.setStatus(backlogIssue.id, IssueStatus.Active);
      issueService.transitionToStage(backlogIssue.id, Stage.Backlog);

      epicService.addIssue(projectId, epic.id, backlogIssue.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail?.progress.nextIssue?.id).toBe(backlogIssue.id);
      expect(detail?.progress.readyToMarkDone).toBe(false);
    });

    it('should indicate ready to mark done when no next issue exists', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const deliveredIssue = issueService.create({ projectId, title: 'Delivered Issue' });
      issueService.setStatus(deliveredIssue.id, IssueStatus.Closed);

      epicService.addIssue(projectId, epic.id, deliveredIssue.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail?.progress.nextIssue).toBeNull();
      expect(detail?.progress.readyToMarkDone).toBe(true);
    });

    it('should treat interrupted issues as blocked next work', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const activeIssue = issueService.create({ projectId, title: 'Active Issue' });
      issueService.setStatus(activeIssue.id, IssueStatus.Active);
      issueService.transitionToStage(activeIssue.id, Stage.Check);

      const interruptedIssue = issueService.create({ projectId, title: 'Interrupted Issue' });
      issueService.transitionToStage(interruptedIssue.id, Stage.Build);
      issueService.setStatus(interruptedIssue.id, IssueStatus.Interrupted);

      epicService.addIssue(projectId, epic.id, activeIssue.id);
      epicService.addIssue(projectId, epic.id, interruptedIssue.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail?.progress.blockedIssues).toContain(interruptedIssue.id);
      expect(detail?.progress.nextIssue?.id).toBe(interruptedIssue.id);
      expect(detail?.progress.readyToMarkDone).toBe(false);
    });

    it('should not treat paused work as next issue', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const pausedIssue = issueService.create({ projectId, title: 'Paused Issue' });
      issueService.transitionToStage(pausedIssue.id, Stage.Check);
      issueService.setStatus(pausedIssue.id, IssueStatus.Paused);

      epicService.addIssue(projectId, epic.id, pausedIssue.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail?.progress.activeIssues).not.toContain(pausedIssue.id);
      expect(detail?.progress.nextIssue).toBeNull();
      expect(detail?.progress.readyToMarkDone).toBe(false);
    });
  });

  describe('done and close lifecycle actions', () => {
    it('should mark Epic done without changing linked issues', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Test Issue' });
      issueService.setStatus(issue.id, IssueStatus.Active);
      epicService.addIssue(projectId, epic.id, issue.id);

      const result = epicService.markDone(projectId, epic.id);

      expect(result?.status).toBe(EpicStatus.Done);
      expect(issueService.getById(issue.id)?.status).toBe(IssueStatus.Active);
    });

    it('should close Epic without changing linked issues', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Test Issue' });
      issueService.setStatus(issue.id, IssueStatus.Active);
      epicService.addIssue(projectId, epic.id, issue.id);

      const result = epicService.close(projectId, epic.id);

      expect(result?.status).toBe(EpicStatus.Closed);
      expect(issueService.getById(issue.id)?.status).toBe(IssueStatus.Active);
    });

    it('should throw when marking non-active Epic done', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });
      epicService.markDone(projectId, epic.id);

      expect(() => epicService.markDone(projectId, epic.id))
        .toThrow('Only active Epics can be marked done');
    });

    it('should throw when closing already closed Epic', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });
      epicService.close(projectId, epic.id);

      expect(() => epicService.close(projectId, epic.id))
        .toThrow('Epic is already closed');
    });

    it('should not affect issue stage through done lifecycle', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Test Issue' });
      issueService.setStatus(issue.id, IssueStatus.Active);
      issueService.transitionToStage(issue.id, Stage.Check);
      epicService.addIssue(projectId, epic.id, issue.id);

      epicService.markDone(projectId, epic.id);

      const updatedIssue = issueService.getById(issue.id);
      expect(updatedIssue?.stage).toBe(Stage.Check);
      expect(updatedIssue?.status).toBe(IssueStatus.Active);
    });
  });

  describe('issue detail primary Epic data', () => {
    it('should return primary Epic data for linked issue', () => {
      const epic = epicService.create({ projectId, title: 'Test Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Test Issue' });
      epicService.addIssue(projectId, epic.id, issue.id);

      const primaryEpic = epicService.getIssueEpic(projectId, issue.id);

      expect(primaryEpic).toBeDefined();
      expect(primaryEpic?.id).toBe(epic.id);
      expect(primaryEpic?.title).toBe('Test Epic');
      expect(primaryEpic?.status).toBe(EpicStatus.Active);
      expect(primaryEpic?.priority).toBe('p1');
    });

    it('should return null for unlinked issue', () => {
      const issue = issueService.create({ projectId, title: 'Unlinked Issue' });

      const primaryEpic = epicService.getIssueEpic(projectId, issue.id);

      expect(primaryEpic).toBeNull();
    });

    it('should not mutate issue on add/remove', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Test Issue' });
      issueService.setStatus(issue.id, IssueStatus.Active);

      epicService.addIssue(projectId, epic.id, issue.id);
      expect(issueService.getById(issue.id)?.status).toBe(IssueStatus.Active);

      epicService.removeIssue(projectId, epic.id, issue.id);
      expect(issueService.getById(issue.id)?.status).toBe(IssueStatus.Active);
    });
  });

  describe('workflow isolation', () => {
    it('should not expose Epics as issues in repo', () => {
      epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issues = issueRepo.findAll();
      expect(issues).toHaveLength(0);
    });

    it('should not allow starting an Epic', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      // Epics don't have a start method - they are not issues
      const found = epicRepo.findById(projectId, epic.id);
      expect(found).toBeDefined();
      expect((found as any).stage).toBeUndefined();
    });

    it('Epic repo should only contain Epic-specific fields', () => {
      const epic = epicService.create({ projectId, title: 'Test Epic', description: 'Desc', priority: 'p2' });

      const stored = epicRepo.findById(projectId, epic.id);
      expect(stored).toBeDefined();
      expect((stored as any).title).toBe('Test Epic');
      expect((stored as any).status).toBe(EpicStatus.Active);
      expect((stored as any).priority).toBe('p2');
      expect((stored as any).stage).toBeUndefined();
      expect((stored as any).runState).toBeUndefined();
      expect((stored as any).worktree).toBeUndefined();
    });
  });

  describe('removeIssue', () => {
    it('should remove issue from Epic without mutating issue', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Test Issue' });
      issueService.setStatus(issue.id, IssueStatus.Active);
      epicService.addIssue(projectId, epic.id, issue.id);

      epicService.removeIssue(projectId, epic.id, issue.id);

      const detail = epicService.getById(projectId, epic.id);
      expect(detail?.linkedIssues).toHaveLength(0);
      expect(issueService.getById(issue.id)?.status).toBe(IssueStatus.Active);
    });

    it('should throw when removing non-linked issue', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue = issueService.create({ projectId, title: 'Test Issue' });

      expect(() => epicService.removeIssue(projectId, epic.id, issue.id))
        .toThrow('Issue is not linked to this Epic');
    });

    it('should throw when Epic not found on remove', () => {
      const issue = issueService.create({ projectId, title: 'Test Issue' });

      expect(() => epicService.removeIssue(projectId, 'non-existent', issue.id))
        .toThrow('Epic not found');
    });
  });

  describe('blocked and active issue projection', () => {
    it('should track blocked issues in progress', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const issue1 = issueService.create({ projectId, title: 'Blocked Issue' });
      issueService.setStatus(issue1.id, IssueStatus.Blocked);

      const issue2 = issueService.create({ projectId, title: 'Active Issue' });
      issueService.setStatus(issue2.id, IssueStatus.Active);
      issueService.transitionToStage(issue2.id, Stage.Check);

      epicService.addIssue(projectId, epic.id, issue1.id);
      epicService.addIssue(projectId, epic.id, issue2.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail?.progress.blockedIssues).toContain(issue1.id);
      expect(detail?.progress.activeIssues).toContain(issue2.id);
    });

    it('should only count active non-backlog issues as active', () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });

      const backlogIssue = issueService.create({ projectId, title: 'Backlog Issue' });
      issueService.setStatus(backlogIssue.id, IssueStatus.Active);
      issueService.transitionToStage(backlogIssue.id, Stage.Backlog);

      const activeIssue = issueService.create({ projectId, title: 'Active Issue' });
      issueService.setStatus(activeIssue.id, IssueStatus.Active);
      issueService.transitionToStage(activeIssue.id, Stage.Check);

      epicService.addIssue(projectId, epic.id, backlogIssue.id);
      epicService.addIssue(projectId, epic.id, activeIssue.id);

      const detail = epicService.getById(projectId, epic.id);

      expect(detail?.progress.blockedIssues).toHaveLength(0);
      expect(detail?.progress.activeIssues).toHaveLength(1);
      expect(detail?.progress.activeIssues[0]).toBe(activeIssue.id);
    });
  });

  describe('API membership errors', () => {
    it('should return a structured 400 when title is invalid during epic creation', async () => {
      const app = new Hono();
      app.route('/api/epics', createEpicRoutes(epicService, projectService));

      const response = await app.request('/api/epics', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ title: '   ', description: 'Desc', priority: 'p1' }),
      });

      expect(response.status).toBe(400);
      const body = await response.json();
      expect(body).toMatchObject({
        success: false,
        error: 'title is required and must be a non-empty string',
        code: 'VALIDATION_ERROR',
        details: { field: 'title' },
      });
    });

    it('should return a structured 400 when description is invalid during epic creation', async () => {
      const app = new Hono();
      app.route('/api/epics', createEpicRoutes(epicService, projectService));

      const response = await app.request('/api/epics', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ title: 'Epic', priority: 'p1' }),
      });

      expect(response.status).toBe(400);
      const body = await response.json();
      expect(body).toMatchObject({
        success: false,
        error: 'description is required and must be a string',
        code: 'VALIDATION_ERROR',
        details: { field: 'description' },
      });
    });

    it('should return a structured 400 when priority is invalid during epic creation', async () => {
      const app = new Hono();
      app.route('/api/epics', createEpicRoutes(epicService, projectService));

      const response = await app.request('/api/epics', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ title: 'Epic', description: 'Desc', priority: 'invalid' }),
      });

      expect(response.status).toBe(400);
      const body = await response.json();
      expect(body).toMatchObject({
        success: false,
        error: 'priority is required and must be one of: p0, p1, p2, p3, p4',
        code: 'VALIDATION_ERROR',
        details: { field: 'priority' },
      });
    });

    it('should return a structured 409 when duplicate membership is detected', async () => {
      const epic1 = epicService.create({ projectId, title: 'Epic 1', description: 'Desc', priority: 'p1' });
      const epic2 = epicService.create({ projectId, title: 'Epic 2', description: 'Desc', priority: 'p1' });
      const issue = issueService.create({ projectId, title: 'Shared Issue' });
      epicService.addIssue(projectId, epic1.id, issue.id);
      const app = new Hono();
      app.route('/api/epics', createEpicRoutes(epicService, projectService));

      const response = await app.request(`/api/epics/${epic2.id}/issues`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ issueId: issue.id }),
      });

      expect(response.status).toBe(409);
      const body = await response.json();
      expect(body).toMatchObject({
        success: false,
        code: 'DUPLICATE_EPIC_MEMBERSHIP',
        details: {
          issueId: issue.id,
          existingEpicId: epic1.id,
          existingEpicTitle: 'Epic 1',
        },
      });
    });

    it('should return a structured 404 when adding a missing issue', async () => {
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });
      const app = new Hono();
      app.route('/api/epics', createEpicRoutes(epicService, projectService));

      const response = await app.request(`/api/epics/${epic.id}/issues`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ issueId: 'missing-issue-id' }),
      });

      expect(response.status).toBe(404);
      const body = await response.json();
      expect(body).toMatchObject({
        success: false,
        error: 'Issue not found',
        code: 'ISSUE_NOT_FOUND',
        details: { issueId: 'missing-issue-id' },
      });
    });

    it('should filter API list responses by projectId query', async () => {
      const otherProject = projectRepo.create({ name: 'Other Project', path: '/other/path' });
      epicService.create({ projectId, title: 'Current Epic', description: 'Desc', priority: 'p1' });
      epicService.create({ projectId: otherProject.id, title: 'Other Epic', description: 'Desc', priority: 'p1' });
      const app = new Hono();
      app.route('/api/epics', createEpicRoutes(epicService, projectService));

      const response = await app.request(`/api/epics?projectId=${encodeURIComponent(otherProject.id)}`);

      expect(response.status).toBe(200);
      const body = await response.json();
      expect(body.success).toBe(true);
      expect(body.data.map((epic: { title: string }) => epic.title)).toEqual(['Other Epic']);
    });

    it('should return a structured 409 when adding a cross-project issue', async () => {
      const otherProject = projectRepo.create({ name: 'Other Project', path: '/other/path' });
      const epic = epicService.create({ projectId, title: 'Epic', description: 'Desc', priority: 'p1' });
      const otherIssue = issueService.create({ projectId: otherProject.id, title: 'Other Issue' });
      const app = new Hono();
      app.route('/api/epics', createEpicRoutes(epicService, projectService));

      const response = await app.request(`/api/epics/${epic.id}/issues`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ issueId: otherIssue.id }),
      });

      expect(response.status).toBe(409);
      const body = await response.json();
      expect(body).toMatchObject({
        success: false,
        error: 'Issue belongs to a different project than this Epic',
        code: 'CROSS_PROJECT_EPIC_MEMBERSHIP',
        details: {
          issueId: otherIssue.id,
          epicProjectId: projectId,
          issueProjectId: otherProject.id,
        },
      });
    });
  });
});
