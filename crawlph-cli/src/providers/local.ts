import { IssueProvider, CreateIssueData } from './interface';
import { Issue, Stage, IssueStatus } from '../types';
import { IssueRepo } from '../db';

export class LocalProvider implements IssueProvider {
  constructor(private issueRepo: IssueRepo) {}

  async getIssues(projectId: string): Promise<Issue[]> {
    return this.issueRepo.findAll({ projectId });
  }

  async getIssue(projectId: string, number: number): Promise<Issue | null> {
    return this.issueRepo.findByNumber(projectId, number);
  }

  async createIssue(projectId: string, data: CreateIssueData): Promise<Issue> {
    const number = this.issueRepo.getNextNumber(projectId);
    return this.issueRepo.create({
      number,
      projectId,
      title: data.title,
      body: data.body,
    });
  }

  async updateStage(issueId: string, stage: Stage): Promise<Issue | null> {
    return this.issueRepo.updateStage(issueId, stage);
  }

  async updateStatus(issueId: string, status: IssueStatus): Promise<Issue | null> {
    return this.issueRepo.updateStatus(issueId, status);
  }
}
