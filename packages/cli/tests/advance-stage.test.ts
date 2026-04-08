import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { DatabaseManager, resetDatabase, closeDatabase } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { IssueRepo } from '../src/db/issue-repo';
import { ProjectRepo } from '../src/db/project-repo';
import { createAdvanceStageTool } from '../src/tools/advance-stage';
import { Stage } from '../src/types';

describe('advance-stage transitions', () => {
  let db: DatabaseManager;
  let issueRepo: IssueRepo;
  let projectRepo: ProjectRepo;
  let projectId: string;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    issueRepo = new IssueRepo(db);
    projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'test-project', path: '/test' });
    projectId = project.id;
  });

  afterEach(() => {
    closeDatabase();
  });

  function createIssue(stage: Stage) {
    const issue = issueRepo.create({
      number: 42,
      projectId,
      title: 'Test Issue',
    });
    if (stage !== Stage.Draft) {
      issueRepo.updateStage(issue.id, stage);
    }
    return issueRepo.findById(issue.id)!;
  }

  it('should allow plan → build transition', async () => {
    const issue = createIssue(Stage.Plan);
    const tool = createAdvanceStageTool({ issue, issueRepo });
    const result = await tool.definition.execute({ stage: 'build' });
    expect(result).toContain('advanced from "plan" to "build"');
    expect(issueRepo.findById(issue.id)!.stage).toBe(Stage.Build);
  });

  it('should allow plan → review transition', async () => {
    const issue = createIssue(Stage.Plan);
    const tool = createAdvanceStageTool({ issue, issueRepo });
    const result = await tool.definition.execute({ stage: 'review' });
    expect(result).toContain('advanced from "plan" to "review"');
    expect(issueRepo.findById(issue.id)!.stage).toBe(Stage.Review);
  });

  it('should allow review → build transition', async () => {
    const issue = createIssue(Stage.Review);
    const tool = createAdvanceStageTool({ issue, issueRepo });
    const result = await tool.definition.execute({ stage: 'build' });
    expect(result).toContain('advanced from "review" to "build"');
    expect(issueRepo.findById(issue.id)!.stage).toBe(Stage.Build);
  });

  it('should reject review → plan transition', async () => {
    const issue = createIssue(Stage.Review);
    const tool = createAdvanceStageTool({ issue, issueRepo });
    const result = await tool.definition.execute({ stage: 'plan' });
    expect(result).toContain('Error');
    expect(result).toContain('cannot advance');
  });

  it('should reject build → review transition', async () => {
    const issue = createIssue(Stage.Build);
    const tool = createAdvanceStageTool({ issue, issueRepo });
    const result = await tool.definition.execute({ stage: 'review' });
    expect(result).toContain('Error');
    expect(result).toContain('cannot advance');
  });

  it('should allow draft → plan transition', async () => {
    const issue = createIssue(Stage.Draft);
    const tool = createAdvanceStageTool({ issue, issueRepo });
    const result = await tool.definition.execute({ stage: 'plan' });
    expect(result).toContain('advanced from "draft" to "plan"');
  });

  it('should allow build → check transition', async () => {
    const issue = createIssue(Stage.Build);
    const tool = createAdvanceStageTool({ issue, issueRepo });
    const result = await tool.definition.execute({ stage: 'check' });
    expect(result).toContain('advanced from "build" to "check"');
  });

  it('should emit approval_requested for review stage (dynamic default)', async () => {
    const issue = createIssue(Stage.Plan);
    const approvals: any[] = [];
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-adv-'));
    const tool = createAdvanceStageTool({
      issue,
      issueRepo,
      worktreePath: tmpDir,
      eventBus: {
        emit: (event: string, data: any) => {
          if (event === 'approval_requested') approvals.push(data);
        },
        on: () => {},
        off: () => {},
      } as any,
    });
    await tool.definition.execute({ stage: 'review' });
    fs.rmSync(tmpDir, { recursive: true, force: true });
    expect(approvals.length).toBe(1);
    expect(approvals[0].stage).toBe(Stage.Review);
  });

  it('should emit approval_requested for build from review', async () => {
    const issue = createIssue(Stage.Review);
    const approvals: any[] = [];
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-adv-'));
    const tool = createAdvanceStageTool({
      issue,
      issueRepo,
      worktreePath: tmpDir,
      eventBus: {
        emit: (event: string, data: any) => {
          if (event === 'approval_requested') approvals.push(data);
        },
        on: () => {},
        off: () => {},
      } as any,
    });
    await tool.definition.execute({ stage: 'build' });
    fs.rmSync(tmpDir, { recursive: true, force: true });
    expect(approvals.length).toBe(1);
    expect(approvals[0].stage).toBe(Stage.Build);
  });

  it('should not emit approval for plan stage', async () => {
    const issue = createIssue(Stage.Draft);
    const approvals: any[] = [];
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-adv-'));
    const tool = createAdvanceStageTool({
      issue,
      issueRepo,
      worktreePath: tmpDir,
      eventBus: {
        emit: (event: string, data: any) => {
          if (event === 'approval_requested') approvals.push(data);
        },
        on: () => {},
        off: () => {},
      } as any,
    });
    await tool.definition.execute({ stage: 'plan' });
    fs.rmSync(tmpDir, { recursive: true, force: true });
    expect(approvals.length).toBe(0);
  });
});
