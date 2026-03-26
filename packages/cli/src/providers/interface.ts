import { Issue, Stage, IssueStatus } from '../types';

export interface CreateIssueData {
  title: string;
  body?: string;
}

export interface IssueProvider {
  getIssues(projectId: string): Promise<Issue[]>;
  getIssue(projectId: string, number: number): Promise<Issue | null>;
  createIssue(projectId: string, data: CreateIssueData): Promise<Issue>;
  updateStage(issueId: string, stage: Stage): Promise<Issue | null>;
  updateStatus(issueId: string, status: IssueStatus): Promise<Issue | null>;
}

export interface IssueProviderConfig {
  type: 'local' | 'github' | 'gitlab';
  [key: string]: unknown;
}
