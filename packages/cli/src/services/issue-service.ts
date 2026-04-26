import { Issue, Stage, IssueStatus, Comment, Priority } from '../types';
import { IssueRepo, CommentRepo } from '../db';
import { Log } from '../util/log';

const log = Log.create({ service: 'issue' });

export interface CreateIssueInput {
  projectId: string;
  title: string;
  body?: string;
  labels?: string[];
  priority?: Priority;
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
      priority: input.priority || 'p2',
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

  getByPriority(projectId: string, priority: Priority): Issue[] {
    return this.issueRepo.findAll({ projectId, priority });
  }

  getActive(projectId: string): Issue[] {
    return this.issueRepo.findActive(projectId);
  }

  transitionToStage(issueId: string, stage: Stage): Issue | null {
    const current = this.issueRepo.findById(issueId);
    const result = this.issueRepo.updateStage(issueId, stage);
    if (result && current) {
      log.info('Stage transition', { issueNumber: result.number, fromStage: current.stage, toStage: stage });
    }
    return result;
  }

  transitionToStageByNumber(projectId: string, number: number, stage: Stage): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    const fromStage = issue.stage;
    const updated = this.issueRepo.updateStage(issue.id, stage);
    if (!updated) return null;

    log.info('Stage transition', { issueNumber: number, fromStage, toStage: stage });

    return this.issueRepo.findByNumber(projectId, number);
  }

  setStatus(issueId: string, status: IssueStatus): Issue | null {
    const current = this.issueRepo.findById(issueId);
    const result = this.issueRepo.updateStatus(issueId, status);
    if (result && current) {
      log.info('Status transition', { issueNumber: result.number, fromStatus: current.status, toStatus: status });
    }
    return result;
  }

  pause(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Paused);
  }

  resume(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    if (issue.status === IssueStatus.Completed) return null;
    if (issue.status === IssueStatus.Closed) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
  }

  reopen(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    if (issue.status !== IssueStatus.Closed && issue.status !== IssueStatus.Blocked && issue.status !== IssueStatus.Paused && issue.status !== IssueStatus.Interrupted) return null;
    
    this.issueRepo.clearApprovalState(issue.id);
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Active);
  }

  block(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Blocked);
  }

  close(projectId: string, number: number): Issue | null {
    const issue = this.issueRepo.findByNumber(projectId, number);
    if (!issue) return null;
    
    return this.issueRepo.updateStatus(issue.id, IssueStatus.Closed);
  }

  update(issueId: string, data: Partial<{ title: string; body: string; stage: Stage; status: IssueStatus; labels: string[]; priority: Priority }>): Issue | null {
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
