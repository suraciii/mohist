import { Stage, IssueStatus } from '../types';

export const STAGE_TRANSITIONS: Record<Stage, Stage | null> = {
  [Stage.Draft]: Stage.Designing,
  [Stage.Designing]: Stage.WaitingDesignReview,
  [Stage.WaitingDesignReview]: Stage.Implementing,
  [Stage.Implementing]: Stage.WaitingReview,
  [Stage.WaitingReview]: Stage.Merging,
  [Stage.Merging]: Stage.Done,
  [Stage.Done]: null
};

export function canTransitionTo(currentStage: Stage, targetStage: Stage): boolean {
  const nextStage = STAGE_TRANSITIONS[currentStage];
  return nextStage === targetStage;
}

export function getNextStage(currentStage: Stage): Stage | null {
  return STAGE_TRANSITIONS[currentStage];
}

export function getStageLabel(stage: Stage): string {
  return `crawlph:stage/${stage}`;
}

export function getStatusLabel(status: IssueStatus): string {
  return `crawlph:status/${status}`;
}

export function parseStageFromLabel(label: string): Stage | null {
  const prefix = 'crawlph:stage/';
  if (label.startsWith(prefix)) {
    const stage = label.substring(prefix.length);
    return Object.values(Stage).includes(stage as Stage) ? (stage as Stage) : null;
  }
  return null;
}

export function parseStatusFromLabel(label: string): IssueStatus | null {
  const prefix = 'crawlph:status/';
  if (label.startsWith(prefix)) {
    const status = label.substring(prefix.length);
    return Object.values(IssueStatus).includes(status as IssueStatus)
      ? (status as IssueStatus)
      : null;
  }
  return null;
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
