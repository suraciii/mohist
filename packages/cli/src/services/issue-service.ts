import { Issue, Stage, IssueStatus } from '../types';
import { IssueRepo, getDatabase } from '../db';

export interface CreateIssueInput {
  projectId: string;
  title: string;
  body?: string;
}

export class IssueService {
  constructor(
    private issueRepo: IssueRepo
  ) {}

  create(input: CreateIssueInput): Issue {
    const number = this.issueRepo.getNextNumber(input.projectId);
    
    return this.issueRepo.create({
      number,
      projectId: input.projectId,
      title: input.title,
      body: input.body,
    });
  }

  getById(id: string): Issue | null {
    return this.issueRepo.findById(id);
  }

  getByNumber(projectId: string, number: number): Issue | null {
    return this.issueRepo.findByNumber(projectId, number);
  }

  getByProject(projectId: string): Issue[] {
    return this.issueRepo.findAll({ projectId });
  }

  getByStage(projectId: string, stage: Stage): Issue[] {
    return this.issueRepo.findByStage(projectId, stage);
  }

  getByStatus(projectId: string, status: IssueStatus): Issue[] {
    return this.issueRepo.findByStatus(projectId, status);
  }

  getActive(projectId: string): Issue[] {
    return this.issueRepo.findActive(projectId);
  }

  transitionToStage(issueId: string, stage: Stage): Issue | null {
    return this.issueRepo.updateStage(issueId, stage);
  }

  transitionToStageByNumber(projectId: string, number: number, stage: Stage): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    const issueRow = this.findIssueRowId(projectId, number);
    if (!issueRow) return null;
    
    const updated = this.issueRepo.updateStage(issueRow, stage);
    if (!updated) return null;
    
    return this.issueRepo.findByNumber(projectId, number);
  }

  private findIssueRowId(projectId: string, number: number): string | null {
    const db = getDatabase();
    const row = db.get<{ id: string }>(
      'SELECT id FROM issues WHERE project_id = ? AND number = ?',
      [projectId, number]
    );
    return row?.id || null;
  }

  setStatus(issueId: string, status: IssueStatus): Issue | null {
    return this.issueRepo.updateStatus(issueId, status);
  }

  pause(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    const issueRow = this.findIssueRowId(projectId, number);
    if (!issueRow) return null;
    
    return this.issueRepo.updateStatus(issueRow, IssueStatus.Paused);
  }

  resume(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    const issueRow = this.findIssueRowId(projectId, number);
    if (!issueRow) return null;
    
    return this.issueRepo.updateStatus(issueRow, IssueStatus.Active);
  }

  block(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    const issueRow = this.findIssueRowId(projectId, number);
    if (!issueRow) return null;
    
    return this.issueRepo.updateStatus(issueRow, IssueStatus.Blocked);
  }

  update(issueId: string, data: Partial<{ title: string; body: string; stage: Stage; status: IssueStatus }>): Issue | null {
    return this.issueRepo.update(issueId, data);
  }

  delete(issueId: string): boolean {
    return this.issueRepo.deleteCascade(issueId);
  }

  deleteByProject(projectId: string): number {
    return this.issueRepo.deleteByProjectCascade(projectId);
  }

  count(projectId: string): number {
    return this.issueRepo.count(projectId);
  }
}
