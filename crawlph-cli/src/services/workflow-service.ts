import { Stage, IssueStatus, Issue } from '../types';
import { IssueService } from './issue-service';

export interface TransitionResult {
  success: boolean;
  issue?: Issue;
  error?: string;
}

export const STAGE_ORDER: Stage[] = [
  Stage.Draft,
  Stage.Designing,
  Stage.WaitingDesignReview,
  Stage.Implementing,
  Stage.WaitingReview,
  Stage.Done,
];

export const USER_APPROVAL_STAGES: Stage[] = [
  Stage.WaitingDesignReview,
  Stage.WaitingReview,
];

export class WorkflowService {
  constructor(private issueService: IssueService) {}

  canTransition(from: Stage, to: Stage): boolean {
    const fromIndex = STAGE_ORDER.indexOf(from);
    const toIndex = STAGE_ORDER.indexOf(to);
    
    if (fromIndex === -1 || toIndex === -1) return false;
    
    return toIndex === fromIndex + 1;
  }

  getNextStage(current: Stage): Stage | null {
    const index = STAGE_ORDER.indexOf(current);
    if (index === -1 || index >= STAGE_ORDER.length - 1) return null;
    return STAGE_ORDER[index + 1];
  }

  getPreviousStage(current: Stage): Stage | null {
    const index = STAGE_ORDER.indexOf(current);
    if (index <= 0) return null;
    return STAGE_ORDER[index - 1];
  }

  requiresUserApproval(stage: Stage): boolean {
    return USER_APPROVAL_STAGES.includes(stage);
  }

  startProcessing(projectId: string, issueNumber: number): TransitionResult {
    const issue = this.issueService.getByNumber(projectId, issueNumber);
    if (!issue) {
      return { success: false, error: `Issue #${issueNumber} not found` };
    }

    if (issue.status === IssueStatus.Paused) {
      return { success: false, error: `Issue #${issueNumber} is paused. Resume it first.` };
    }

    if (issue.stage !== Stage.Draft) {
      return { success: false, error: `Issue #${issueNumber} is not in draft stage` };
    }

    const nextStage = this.getNextStage(issue.stage);
    if (!nextStage) {
      return { success: false, error: `Cannot advance from stage ${issue.stage}` };
    }

    const updated = this.issueService.transitionToStageByNumber(projectId, issueNumber, nextStage);
    if (!updated) {
      return { success: false, error: `Failed to transition issue #${issueNumber}` };
    }

    return { success: true, issue: updated };
  }

  approve(projectId: string, issueNumber: number): TransitionResult {
    const issue = this.issueService.getByNumber(projectId, issueNumber);
    if (!issue) {
      return { success: false, error: `Issue #${issueNumber} not found` };
    }

    if (!this.requiresUserApproval(issue.stage)) {
      return { 
        success: false, 
        error: `Issue #${issueNumber} is at stage ${issue.stage}, which does not require approval` 
      };
    }

    const nextStage = this.getNextStage(issue.stage);
    if (!nextStage) {
      return { success: false, error: `Cannot advance from stage ${issue.stage}` };
    }

    const updated = this.issueService.transitionToStageByNumber(projectId, issueNumber, nextStage);
    if (!updated) {
      return { success: false, error: `Failed to transition issue #${issueNumber}` };
    }

    return { success: true, issue: updated };
  }

  advance(projectId: string, issueNumber: number): TransitionResult {
    const issue = this.issueService.getByNumber(projectId, issueNumber);
    if (!issue) {
      return { success: false, error: `Issue #${issueNumber} not found` };
    }

    const nextStage = this.getNextStage(issue.stage);
    if (!nextStage) {
      return { success: false, error: `Cannot advance from stage ${issue.stage}` };
    }

    const updated = this.issueService.transitionToStageByNumber(projectId, issueNumber, nextStage);
    if (!updated) {
      return { success: false, error: `Failed to transition issue #${issueNumber}` };
    }

    return { success: true, issue: updated };
  }

  getStageInfo(stage: Stage): {
    name: string;
    description: string;
    requiresApproval: boolean;
    nextStage: Stage | null;
  } {
    const descriptions: Record<Stage, string> = {
      [Stage.Draft]: 'Issue created, waiting to start processing',
      [Stage.Designing]: 'Agent is generating the design document',
      [Stage.WaitingDesignReview]: 'Design complete, waiting for user approval',
      [Stage.Implementing]: 'Agent is implementing the code',
      [Stage.WaitingReview]: 'Implementation complete, waiting for user approval',
      [Stage.Done]: 'Issue processing complete',
    };

    return {
      name: stage,
      description: descriptions[stage] || 'Unknown stage',
      requiresApproval: this.requiresUserApproval(stage),
      nextStage: this.getNextStage(stage),
    };
  }

  getProgress(stage: Stage): { current: number; total: number; percentage: number } {
    const index = STAGE_ORDER.indexOf(stage);
    const total = STAGE_ORDER.length;
    const current = index + 1;
    
    return {
      current,
      total,
      percentage: Math.round((current / total) * 100),
    };
  }
}
