import type { StageCompletionGuard } from './run/types';

export class WorkflowDomainError extends Error {
  constructor(
    message: string,
    readonly details?: { stageCompletionGuard?: StageCompletionGuard },
  ) {
    super(message);
  }
}
