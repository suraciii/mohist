export enum Stage {
  Draft = 'draft',
  Designing = 'designing',
  WaitingDesignReview = 'waiting-design-review',
  Implementing = 'implementing',
  WaitingReview = 'waiting-review',
  Merging = 'merging',
  Done = 'done'
}

export enum IssueStatus {
  Active = 'active',
  Paused = 'paused',
  Blocked = 'blocked'
}

export interface Issue {
  number: number;
  title: string;
  body?: string;
  stage: Stage;
  status: IssueStatus;
  labels: string[];
  projectId: string;
  prNumber?: number;
  url: string;
  createdAt: string;
  updatedAt: string;
}

export interface Project {
  id: string;
  name: string;
  repo: string;
  path: string;
  createdAt: string;
  updatedAt: string;
}

export interface Task {
  id: string;
  issueNumber: number;
  projectId: string;
  stage: Stage;
  status: 'pending' | 'running' | 'completed' | 'failed';
  agentPid?: number;
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

export interface Config {
  githubToken?: string;
  serverPort: number;
  pollInterval: number;
  maxConcurrentAgents: number;
  agentTimeout: number;
}

export interface ProjectConfig {
  repo: string;
  labels: {
    stagePrefix: string;
    statusPrefix: string;
  };
}

export interface ServerState {
  isRunning: boolean;
  pid?: number;
  port: number;
  startedAt?: string;
  activeTasks: number;
  queuedTasks: number;
}

export interface ApiResponse<T = any> {
  success: boolean;
  data?: T;
  error?: string;
}

export interface GitHubLabel {
  name: string;
  color: string;
  description?: string;
}

export interface PullRequest {
  number: number;
  title: string;
  state: 'open' | 'closed';
  draft: boolean;
  mergeable?: boolean;
  merged: boolean;
  approved: boolean;
  headBranch: string;
  baseBranch: string;
  url: string;
  issueNumber?: number;
  createdAt: string;
  updatedAt: string;
}
