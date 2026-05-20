import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { IssueTaskQueueRepo } from '../src/db/issue-task-queue-repo';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { EventBus } from '../src/services/event-bus';
import { IssueService } from '../src/services/issue-service';
import { IssuePrerequisiteService } from '../src/services/issue-prerequisite-service';
import { IssueStartPrerequisiteRepo } from '../src/db/issue-start-prerequisite-repo';
import { Stage, IssueStatus, MergeState } from '../src/types';
import * as fs from 'fs';
import * as path from 'path';

let projectCounter = 0;

function touchFetchCache(projectPath: string) {
  const gitDir = path.join(projectPath, '.git');
  fs.mkdirSync(gitDir, { recursive: true });
  fs.writeFileSync(path.join(gitDir, 'mohist-last-fetch'), Date.now().toString(), 'utf-8');
}

function createHangingWorktreeManager() {
  return {
    exists: () => false,
    create: () => new Promise(() => {}),
    getPath: () => '/tmp/worktree/issue-1',
    remove: () => Promise.resolve(),
    canFastForward: () => Promise.resolve(true),
    rebaseOntoMaster: () => Promise.resolve({ success: true, conflicts: [] }),
    abortRebase: () => Promise.resolve(),
  };
}

describe('Start Eligibility Queue Execution', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let taskQueueRepo: IssueTaskQueueRepo;
  let prerequisiteRepo: IssueStartPrerequisiteRepo;
  let prerequisiteService: IssuePrerequisiteService;

  beforeEach(() => {
    projectCounter = 0;
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    issueService = new IssueService(issueRepo);
    eventBus = new EventBus();
    taskQueueRepo = new IssueTaskQueueRepo(db);
    prerequisiteRepo = new IssueStartPrerequisiteRepo(db);
    prerequisiteService = new IssuePrerequisiteService(issueRepo, prerequisiteRepo);
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

  function createService(maxConcurrent = 8, wtManager?: any) {
    return new AgentRunnerService(
      eventBus, undefined, issueRepo, maxConcurrent,
      undefined, undefined, projectRepo,
      wtManager ?? createHangingWorktreeManager(),
      taskQueueRepo,
      undefined, undefined, undefined, undefined, undefined,
      prerequisiteService
    );
  }

  async function waitForTask(taskId: string, timeoutMs = 1000) {
    const start = Date.now();
    while (Date.now() - start < timeoutMs) {
      const record = taskQueueRepo.findById(taskId);
      if (record && record.status !== 'running') {
        return record;
      }
      await new Promise((r) => setTimeout(r, 25));
    }
    return taskQueueRepo.findById(taskId);
  }

  describe('executeStartPipelineTask - prerequisite backstop', () => {
    it('should skip start-pipeline when issue is waiting for prerequisite delivery', async () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const service = createService();
      const result = service.enqueue(issue201.id, 'start-pipeline');

      expect(['pending', 'running']).toContain(result.status);

      const dbRecord = await waitForTask(result.taskId);
      expect(dbRecord!.status).toBe('completed');
      expect(dbRecord!.result).toContain('skipped');
      expect(dbRecord!.result).toContain('waiting for prerequisite');
    });

    it('should not create WorkflowRun when issue is waiting for prerequisite delivery', async () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const service = createService();
      const result = service.enqueue(issue201.id, 'start-pipeline');

      const dbRecord = await waitForTask(result.taskId);
      expect(dbRecord!.result).toContain('skipped');
    });

    it('should not create agent session when issue is waiting for prerequisite delivery', async () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const service = createService();
      const result = service.enqueue(issue201.id, 'start-pipeline');

      const dbRecord = await waitForTask(result.taskId);
      expect(dbRecord!.result).toContain('skipped');
      expect(dbRecord!.result).not.toContain('agent');
    });

    it('should record waiting reason without marking issue as blocked', async () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      const service = createService();
      const result = service.enqueue(issue201.id, 'start-pipeline');

      const dbRecord = await waitForTask(result.taskId);
      expect(dbRecord!.result).toContain(`#${issue200.number}`);

      const issue201Updated = issueRepo.findById(issue201.id);
      expect(issue201Updated!.status).not.toBe(IssueStatus.Blocked);
    });

    it('should proceed normally after prerequisite issue is delivered', async () => {
      const project = setupProject();
      touchFetchCache(project.path);
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      issueRepo.updateStage(issue200.id, Stage.Done);
      issueRepo.updateStatus(issue200.id, IssueStatus.Completed);
      issueRepo.setMergeState(issue200.id, MergeState.Merged);

      const wtManager = {
        exists: () => true,
        create: () => Promise.resolve('/tmp/worktree'),
        getPath: () => '/tmp/worktree',
        remove: () => Promise.resolve(),
        canFastForward: () => Promise.resolve(true),
        rebaseOntoMaster: () => Promise.resolve({ success: true, conflicts: [] }),
        abortRebase: () => Promise.resolve(),
      };

      const service = createService(1, wtManager);
      const result = service.enqueue(issue201.id, 'start-pipeline');

      const dbRecord = await waitForTask(result.taskId);
      expect(dbRecord!.result ?? '').not.toContain('waiting for prerequisite');
      expect(dbRecord!.result ?? '').not.toContain('skipped');
    });

    it('should skip with lifecycle reason when issue has no prerequisites but is not startable', async () => {
      const project = setupProject();
      const issue = setupIssue(project.id, 'Standalone Issue');

      issueRepo.updateStage(issue.id, Stage.Build);

      const service = createService();
      const result = service.enqueue(issue.id, 'start-pipeline');

      const dbRecord = await waitForTask(result.taskId);
      expect(dbRecord!.status).toBe('completed');
      expect(dbRecord!.result).toContain('skipped');
      expect(dbRecord!.result).toContain('Only backlog issues can be started');
    });

    it('should not affect resume-pipeline tasks', async () => {
      const project = setupProject();
      const issue200 = setupIssue(project.id, 'Issue #200');
      const issue201 = setupIssue(project.id, 'Issue #201');

      prerequisiteService.declarePrerequisite(project.id, issue201.number, issue200.number);

      issueRepo.updateStage(issue201.id, Stage.Integrate);

      const service = createService();
      const result = service.enqueue(issue201.id, 'resume-pipeline');

      const dbRecord = await waitForTask(result.taskId);
      expect(dbRecord!.result).not.toContain('waiting for prerequisite');
    });
  });
});
