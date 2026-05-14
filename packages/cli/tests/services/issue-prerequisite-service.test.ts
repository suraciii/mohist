import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../../src/db/database';
import { initializeDatabase } from '../../src/db/migrations';
import { ProjectRepo } from '../../src/db/project-repo';
import { IssueRepo } from '../../src/db/issue-repo';
import { IssueStartPrerequisiteRepo } from '../../src/db/issue-start-prerequisite-repo';
import { IssueService } from '../../src/services/issue-service';
import { IssuePrerequisiteService } from '../../src/services/issue-prerequisite-service';
import { Stage, IssueStatus, MergeState } from '../../src/types';

describe('IssuePrerequisiteService', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let prerequisiteRepo: IssueStartPrerequisiteRepo;
  let issueService: IssueService;
  let prerequisiteService: IssuePrerequisiteService;
  let projectCounter = 0;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    prerequisiteRepo = new IssueStartPrerequisiteRepo(db);
    issueService = new IssueService(issueRepo);
    prerequisiteService = new IssuePrerequisiteService(issueRepo, prerequisiteRepo);
    projectCounter = 0;
  });

  afterEach(() => {
    db.close();
  });

  function setupProject(name?: string) {
    const n = name ?? `project-${++projectCounter}`;
    return projectRepo.create({ name: n, path: `/tmp/${n}`, baseBranch: 'main' });
  }

  function setupIssue(projectId: string, title = 'Test Issue') {
    return issueService.create({ projectId, title });
  }

  function makeDelivered(issueId: string) {
    issueRepo.updateStage(issueId, Stage.Done);
    issueRepo.updateStatus(issueId, IssueStatus.Completed);
    issueRepo.setMergeState(issueId, MergeState.Merged);
  }

  describe('declarePrerequisite', () => {
    it('should record a start prerequisite between two issues', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      const result = prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      expect('error' in result).toBe(false);
      const view = (result as any).prerequisites as any[];
      expect(view.some((p: any) => p.number === issue200.number)).toBe(true);
    });

    it('should return same-issue error when issue requires itself', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');

      const result = prerequisiteService.declarePrerequisite(project.id, issue200.number, issue200.number);

      expect('error' in result).toBe(true);
      expect((result as any).reason).toBe('same-issue');
    });

    it('should reject circular prerequisite declaration', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const result = prerequisiteService.declarePrerequisite(project.id, issue200.number, issue201.number);

      expect('error' in result).toBe(true);
      expect((result as any).reason).toBe('circular-prerequisite');
    });

    it('should reject indirect circular prerequisite (A->B->C->A)', () => {
      const project = setupProject();
      const issueA = setupIssue(project.id, 'Issue A');
      const issueB = setupIssue(project.id, 'Issue B');
      const issueC = setupIssue(project.id, 'Issue C');

      prerequisiteService.declarePrerequisite(project.id, issueB.number, issueA.number);
      prerequisiteService.declarePrerequisite(project.id, issueC.number, issueB.number);

      const result = prerequisiteService.declarePrerequisite(project.id, issueA.number, issueC.number);

      expect('error' in result).toBe(true);
      expect((result as any).reason).toBe('circular-prerequisite');
    });

    it('should return not-found when prerequisite issue does not exist', () => {
      const project = setupProject();
      const issue201 = setupIssue(project.id, 'Issue #201');

      const result = prerequisiteService.declarePrerequisite(project.id, issue201.number, 999);

      expect('error' in result).toBe(true);
      expect((result as any).reason).toBe('not-found');
    });

    it('should return not-found when declaring issue does not exist', () => {
      const project = setupProject();

      const result = prerequisiteService.declarePrerequisite(project.id, 999, 200);

      expect('error' in result).toBe(true);
      expect((result as any).reason).toBe('not-found');
    });
  });

  describe('removePrerequisite', () => {
    it('should remove an existing prerequisite', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      const removed = prerequisiteService.removePrerequisite(project.id, issue201.number, issue200.number);

      expect(removed).toBe(true);
      const view = prerequisiteService.getPrerequisiteView(project.id, issue201);
      expect(view.prerequisites).toHaveLength(0);
    });

    it('should return false when prerequisite does not exist', () => {
      const project = setupProject();
      const issue201 = setupIssue(project.id, 'Issue #201');

      const removed = prerequisiteService.removePrerequisite(project.id, issue201.number, 999);

      expect(removed).toBe(false);
    });
  });

  describe('evaluateStartEligibility', () => {
    it('should report startable=true when issue has no prerequisites', () => {
      const project = setupProject();
      const issue = setupIssue(project.id, 'Standalone Issue');

      const eligibility = prerequisiteService.evaluateStartEligibility(issue);

      expect(eligibility.startable).toBe(true);
      expect(eligibility.reason).toBe('ready');
      expect(eligibility.waitingForDelivery).toHaveLength(0);
    });

    it('should report not-startable-lifecycle when issue is blocked', () => {
      const project = setupProject();
      const issue = setupIssue(project.id, 'Blocked Issue');
      issueRepo.updateStatus(issue.id, IssueStatus.Blocked);

      const eligibility = prerequisiteService.evaluateStartEligibility(issueRepo.findById(issue.id)!);

      expect(eligibility.startable).toBe(false);
      expect(eligibility.reason).toBe('not-startable-lifecycle');
      expect(eligibility.message).toContain('is blocked');
    });

    it('should report not-startable-lifecycle when issue is not in backlog', () => {
      const project = setupProject();
      const issue = setupIssue(project.id, 'Plan Issue');
      issueRepo.updateStage(issue.id, Stage.Plan);

      const eligibility = prerequisiteService.evaluateStartEligibility(issueRepo.findById(issue.id)!);

      expect(eligibility.startable).toBe(false);
      expect(eligibility.reason).toBe('not-startable-lifecycle');
      expect(eligibility.message).toContain('Only backlog issues can be started');
    });

    it('should keep waitingForDelivery in lifecycle rejection output', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      issueRepo.updateStage(issue201.id, Stage.Plan);

      const eligibility = prerequisiteService.evaluateStartEligibility(issueRepo.findById(issue201.id)!);

      expect(eligibility.reason).toBe('not-startable-lifecycle');
      expect(eligibility.waitingForDelivery).toHaveLength(1);
      expect(eligibility.waitingForDelivery[0].number).toBe(issue200.number);
    });

    it('should report startable=false with waitingForDelivery when prerequisite not delivered', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      const eligibility = prerequisiteService.evaluateStartEligibility(issue201);

      expect(eligibility.startable).toBe(false);
      expect(eligibility.reason).toBe('waiting-for-delivery');
      expect(eligibility.waitingForDelivery).toHaveLength(1);
      expect(eligibility.waitingForDelivery[0].number).toBe(issue200.number);
      expect(eligibility.waitingForDelivery[0].delivered).toBe(false);
      expect(eligibility.message).toContain(`#${issue200.number}`);
    });

    it('should report startable=true when prerequisite is delivered', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      makeDelivered(issue200.id);
      const eligibility = prerequisiteService.evaluateStartEligibility(issue201);

      expect(eligibility.startable).toBe(true);
      expect(eligibility.reason).toBe('ready');
      expect(eligibility.waitingForDelivery).toHaveLength(0);
    });

    it('should not consider done-but-not-merged as delivered', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      issueRepo.updateStage(issue200.id, Stage.Done);
      issueRepo.updateStatus(issue200.id, IssueStatus.Completed);

      const eligibility = prerequisiteService.evaluateStartEligibility(issue201);

      expect(eligibility.startable).toBe(false);
      expect(eligibility.waitingForDelivery[0].delivered).toBe(false);
    });

    it('should not consider merged without done stage as delivered', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      issueRepo.setMergeState(issue200.id, MergeState.Merged);

      const eligibility = prerequisiteService.evaluateStartEligibility(issue201);

      expect(eligibility.startable).toBe(false);
      expect(eligibility.waitingForDelivery[0].delivered).toBe(false);
    });

    it('should report multiple waiting prerequisites', () => {
      const project = setupProject();
      const issue199 = setupIssue(project.id, 'Issue #199');
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue199.number);
      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const eligibility = prerequisiteService.evaluateStartEligibility(issue201);

      expect(eligibility.startable).toBe(false);
      expect(eligibility.waitingForDelivery).toHaveLength(2);
    });
  });

  describe('getPrerequisiteView', () => {
    it('should return prerequisites and startEligibility for an issue', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      const view = prerequisiteService.getPrerequisiteView(project.id, issue201);

      expect(view.prerequisites).toHaveLength(1);
      expect(view.prerequisites[0].number).toBe(issue200.number);
      expect(view.startEligibility.startable).toBe(false);
      expect(view.startEligibility.waitingForDelivery[0].number).toBe(issue200.number);
    });

    it('should indicate delivered state for delivered prerequisites', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      makeDelivered(issue200.id);
      const view = prerequisiteService.getPrerequisiteView(project.id, issue201);

      expect(view.prerequisites[0].delivered).toBe(true);
      expect(view.startEligibility.startable).toBe(true);
    });
  });

  describe('getPrerequisiteViews (batched)', () => {
    it('should return prerequisite views for multiple issues', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');
      const issue202 = setupIssue(project.id, 'Issue #202');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      prerequisiteService.declarePrerequisite(project.id, issue202.number, issue200.number);

      const issues = [issue201, issue202];
      const views = prerequisiteService.getPrerequisiteViews(project.id, issues);

      expect(views.has(issue201.id)).toBe(true);
      expect(views.has(issue202.id)).toBe(true);
      expect(views.get(issue201.id)!.prerequisites).toHaveLength(1);
      expect(views.get(issue202.id)!.prerequisites).toHaveLength(1);
    });

    it('should return empty map for empty issues list', () => {
      const project = setupProject();
      const views = prerequisiteService.getPrerequisiteViews(project.id, []);

      expect(views.size).toBe(0);
    });
  });

  describe('assertStartEligible', () => {
    it('should return start eligibility for eligible issue', () => {
      const project = setupProject();
      const issue = setupIssue(project.id, 'Startable Issue');

      const eligibility = prerequisiteService.assertStartEligible(project.id, issue);

      expect(eligibility.startable).toBe(true);
    });

    it('should return start eligibility with waitingForDelivery for non-startable issue', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      const eligibility = prerequisiteService.assertStartEligible(project.id, issue201);

      expect(eligibility.startable).toBe(false);
      expect(eligibility.waitingForDelivery).toHaveLength(1);
    });
  });

  describe('waiting is not blocked status', () => {
    it('should not set issue status to blocked when waiting for delivery', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      prerequisiteService.evaluateStartEligibility(issue201);

      const issue201Updated = issueRepo.findById(issue201.id);
      expect(issue201Updated!.status).toBe(IssueStatus.Active);
      expect(issue201Updated!.status).not.toBe(IssueStatus.Blocked);
    });

    it('should not have blockedReason set due to waiting for delivery', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      prerequisiteService.evaluateStartEligibility(issue201);

      const issue201Updated = issueRepo.findById(issue201.id);
      expect(issue201Updated!.blockedReason ?? null).toBeNull();
    });
  });

  describe('persistence', () => {
    it('should persist prerequisite and reload via new service instance', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const newService = new IssuePrerequisiteService(issueRepo, prerequisiteRepo);
      const view = newService.getPrerequisiteView(project.id, issue201);

      expect(view.prerequisites).toHaveLength(1);
      expect(view.prerequisites[0].number).toBe(issue200.number);
    });

    it('should not interpret task-level tasks.json dependsOn as issue-level start prerequisite', () => {
      const project = setupProject();
      const issue = setupIssue(project.id, 'Issue with tasks.json dependsOn');

      const eligibility = prerequisiteService.evaluateStartEligibility(issue);

      expect(eligibility.startable).toBe(true);
      expect(eligibility.reason).toBe('ready');
    });
  });

  describe('issue-level vs task-level separation', () => {
    it('should not read tasks.json dependsOn as issue-level prerequisite', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      const eligibility = prerequisiteService.evaluateStartEligibility(issue201);

      expect(eligibility.startable).toBe(true);
      expect(eligibility.waitingForDelivery).toHaveLength(0);
    });

    it('should only expose issue-level start prerequisites in getPrerequisiteView', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);
      const view = prerequisiteService.getPrerequisiteView(project.id, issue201);

      expect(view.prerequisites).toHaveLength(1);
      expect(view.prerequisites[0].number).toBe(issue200.number);
      expect(view.startEligibility.waitingForDelivery).toHaveLength(1);
    });

    it('should surface declared prerequisites through view and start guard on the same service instance', () => {
      const project = setupProject();
      const prerequisite = setupIssue(project.id, 'Prerequisite');
      const issue = setupIssue(project.id, 'Dependent');

      prerequisiteService.declarePrerequisite(project.id, issue.number, prerequisite.number);

      const view = prerequisiteService.getPrerequisiteView(project.id, issue);
      const eligibility = prerequisiteService.assertStartEligible(project.id, issue);

      expect(view.prerequisites).toHaveLength(1);
      expect(view.prerequisites[0].number).toBe(prerequisite.number);
      expect(eligibility.startable).toBe(false);
      expect(eligibility.waitingForDelivery[0].number).toBe(prerequisite.number);
    });

    it('should not interpret tasks.json dependsOn as delivered evaluation', () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      makeDelivered(issue200.id);
      const view = prerequisiteService.getPrerequisiteView(project.id, issue201);

      expect(view.prerequisites).toHaveLength(0);
      expect(view.startEligibility.startable).toBe(true);
    });
  });
});
