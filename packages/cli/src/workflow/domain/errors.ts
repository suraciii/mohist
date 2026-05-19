import type { StageCompletionGuard } from './types';

export class WorkflowDomainError extends Error {
  constructor(
    message: string,
    readonly details?: { stageCompletionGuard?: StageCompletionGuard },
  ) {
    super(message);
  }
}
