import type { StageCompletionGuard } from './run';

export class WorkflowDomainError extends Error {
  constructor(
    message: string,
    readonly details?: { stageCompletionGuard?: StageCompletionGuard },
  ) {
    super(message);
  }
}
