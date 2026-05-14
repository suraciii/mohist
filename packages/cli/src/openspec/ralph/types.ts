export type FailureCategory =
  | 'ac_not_met'
  | 'environment'
  | 'dependency'
  | 'timeout'
  | 'timeout_with_wip'
  | 'hang_unrecoverable'
  | 'session_failed';

export interface FailureCategoryConfig {
  maxAttempts: number;
  retryable: boolean;
}

export const FAILURE_CATEGORY_CONFIGS: Record<FailureCategory, FailureCategoryConfig> = {
  ac_not_met: { maxAttempts: 3, retryable: true },
  environment: { maxAttempts: 2, retryable: true },
  dependency: { maxAttempts: 1, retryable: false },
  timeout: { maxAttempts: 3, retryable: true },
  timeout_with_wip: { maxAttempts: 2, retryable: true },
  hang_unrecoverable: { maxAttempts: 1, retryable: false },
  session_failed: { maxAttempts: 2, retryable: true },
};

export interface DependencyValidationResult {
  valid: boolean;
  errors: string[];
}

export function getOrderValue(order: number | undefined): number {
  if (order === undefined) return 999999;
  return order;
}