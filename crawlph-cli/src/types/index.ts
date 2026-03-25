export enum Stage {
  Draft = 'draft',
  Designing = 'designing',
  WaitingDesignReview = 'waiting-design-review',
  Implementing = 'implementing',
  WaitingReview = 'waiting-review',
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
  projectId: string;
  createdAt: string;
  updatedAt: string;
}

export interface Project {
  id: string;
  name: string;
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
  serverPort: number;
  pollInterval: number;
  maxConcurrentAgents: number;
  agentTimeout: number;
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
