import type { StageCompletionGuard } from './workflow-run';

export class WorkflowDomainError extends Error {
  constructor(
    message: string,
    readonly details?: { stageCompletionGuard?: StageCompletionGuard },
  ) {
    super(message);
  }
}
