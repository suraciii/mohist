import { Stage, IssueStatus } from '../types';

export const STAGE_TRANSITIONS: Record<Stage, Stage | null> = {
  [Stage.Draft]: Stage.Designing,
  [Stage.Designing]: Stage.WaitingDesignReview,
  [Stage.WaitingDesignReview]: Stage.Implementing,
  [Stage.Implementing]: Stage.WaitingReview,
  [Stage.WaitingReview]: Stage.Done,
  [Stage.Done]: null
};

export function canTransitionTo(currentStage: Stage, targetStage: Stage): boolean {
  const nextStage = STAGE_TRANSITIONS[currentStage];
  return nextStage === targetStage;
}

export function getNextStage(currentStage: Stage): Stage | null {
  return STAGE_TRANSITIONS[currentStage];
}

export function requiresUserApproval(stage: Stage): boolean {
  return stage === Stage.WaitingDesignReview || stage === Stage.WaitingReview;
}

export function isTerminalStage(stage: Stage): boolean {
  return stage === Stage.Done;
}

export function canStartAgent(stage: Stage, status: IssueStatus): boolean {
  return status === IssueStatus.Active && !requiresUserApproval(stage) && !isTerminalStage(stage);
}
