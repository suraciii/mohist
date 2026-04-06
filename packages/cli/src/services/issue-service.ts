import { Issue, Stage, IssueStatus, Comment } from '../types';
import { IssueRepo, CommentRepo } from '../db';

export interface CreateIssueInput {
  projectId: string;
  title: string;
  body?: string;
  labels?: string[];
}

export class IssueService {
  constructor(
    private issueRepo: IssueRepo,
    private commentRepo: CommentRepo
  ) {}

  create(input: CreateIssueInput): Issue {
    const number = this.issueRepo.getNextNumber(input.projectId);
    
    return this.issueRepo.create({
      number,
      projectId: input.projectId,
      title: input.title,
      body: input.body,
      labels: input.labels,
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
    
    const updated = this.issueRepo.updateStage(issue.id, stage);
    if (!updated) return null;
    
    return this.issueRepo.findByNumber(projectId, number);
  }

  setStatus(issueId: string, status: IssueStatus): Issue | null {
    return this.issueRepo.updateStatus(issueId, status);
  }

  pause(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Paused);
  }

  resume(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
  }

  block(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
  }

  update(issueId: string, data: Partial<{ title: string; body: string; stage: Stage; status: IssueStatus; labels: string[] }>): Issue | null {
    return this.issueRepo.update(issueId, data);
  }

  createComment(issueId: string, body: string): Comment {
    return this.commentRepo.create({ issueId, body });
  }

  getCommentsByIssue(issueId: string): Comment[] {
    return this.commentRepo.findByIssue(issueId);
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
