import { Stage, Issue, Task } from '../types';
import { AgentRunner } from '../agent/runner';
import { getNextStage, canStartAgent } from './issue-workflow';

export interface StageHandler {
  canExecute(issue: Issue): boolean;
  execute(issue: Issue, task: Task): Promise<void>;
  onComplete(issue: Issue): Stage | null;
}

export class DesigningHandler implements StageHandler {
  constructor(private runner: AgentRunner) {}
  
  canExecute(issue: Issue): boolean {
    return canStartAgent(issue.stage, issue.status);
  }
  
  async execute(issue: Issue, task: Task): Promise<void> {
    await this.runner.runDesignerAgent(issue, task);
  }
  
  onComplete(issue: Issue): Stage | null {
    return getNextStage(issue.stage);
  }
}

export class ImplementingHandler implements StageHandler {
  constructor(private runner: AgentRunner) {}
  
  canExecute(issue: Issue): boolean {
    return canStartAgent(issue.stage, issue.status);
  }
  
  async execute(issue: Issue, task: Task): Promise<void> {
    const designPath = `openspec/changes/issue-${issue.number}/design.md`;
    await this.runner.runImplementerAgent(issue, task, designPath);
  }
  
  onComplete(issue: Issue): Stage | null {
    return getNextStage(issue.stage);
  }
}

export class WaitingDesignReviewHandler implements StageHandler {
  canExecute(_issue: Issue): boolean {
    return false;
  }
  
  async execute(_issue: Issue, _task: Task): Promise<void> {
    throw new Error('Cannot execute waiting-design-review stage - requires user approval');
  }
  
  onComplete(issue: Issue): Stage | null {
    return getNextStage(issue.stage);
  }
}

export class WaitingReviewHandler implements StageHandler {
  canExecute(_issue: Issue): boolean {
    return false;
  }
  
  async execute(_issue: Issue, _task: Task): Promise<void> {
    throw new Error('Cannot execute waiting-review stage - requires user approval');
  }
  
  onComplete(issue: Issue): Stage | null {
    return getNextStage(issue.stage);
  }
}

export class MergingHandler implements StageHandler {
  constructor(private githubClient: any) {}
  
  canExecute(issue: Issue): boolean {
    return canStartAgent(issue.stage, issue.status) && !!issue.prNumber;
  }
  
  async execute(issue: Issue, _task: Task): Promise<void> {
    if (!issue.prNumber) {
      throw new Error('No PR associated with issue');
    }
    
    const pr = await this.githubClient.getPullRequest(issue.prNumber);
    
    if (!pr.approved) {
      throw new Error('PR not approved');
    }
    
    if (pr.merged) {
      return;
    }
    
    await this.githubClient.mergePR(issue.prNumber);
  }
  
  onComplete(issue: Issue): Stage | null {
    return getNextStage(issue.stage);
  }
}

export class DoneHandler implements StageHandler {
  canExecute(_issue: Issue): boolean {
    return false;
  }
  
  async execute(_issue: Issue, _task: Task): Promise<void> {
    throw new Error('Cannot execute done stage - this is a terminal state');
  }
  
  onComplete(_issue: Issue): Stage | null {
    return null;
  }
}

export class DraftHandler implements StageHandler {
  canExecute(_issue: Issue): boolean {
    return false;
  }
  
  async execute(_issue: Issue, _task: Task): Promise<void> {
    throw new Error('Cannot execute draft stage - must be started first');
  }
  
  onComplete(issue: Issue): Stage | null {
    return getNextStage(issue.stage);
  }
}

export function getStageHandler(
  stage: Stage,
  runner?: AgentRunner,
  githubClient?: any
): StageHandler {
  switch (stage) {
    case Stage.Draft:
      return new DraftHandler();
    case Stage.Designing:
      if (!runner) throw new Error('AgentRunner required for designing stage');
      return new DesigningHandler(runner);
    case Stage.WaitingDesignReview:
      return new WaitingDesignReviewHandler();
    case Stage.Implementing:
      if (!runner) throw new Error('AgentRunner required for implementing stage');
      return new ImplementingHandler(runner);
    case Stage.WaitingReview:
      return new WaitingReviewHandler();
    case Stage.Merging:
      if (!githubClient) throw new Error('GitHubClient required for merging stage');
      return new MergingHandler(githubClient);
    case Stage.Done:
      return new DoneHandler();
    default:
      throw new Error(`Unknown stage: ${stage}`);
  }
}
