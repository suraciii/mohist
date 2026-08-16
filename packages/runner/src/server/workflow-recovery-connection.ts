import type {
  WorkflowTaskCleanupLeaseRequest,
  WorkflowTaskCleanupLeaseResult,
  WorkflowTaskCleanupOperation,
  WorkflowTaskCleanupOperationResult,
  WorkflowTaskExecutionIdentity,
  WorkflowTaskSourceAdoption,
  WorkflowTaskSourceAdoptionRequest,
  WorkflowTaskSourceAdoptionResult,
  WorkspaceVerification,
} from '../core/types.js'

type RecoveryConnectionHost = {
  postRecovery<T>(path: string, body: unknown, signal: AbortSignal): Promise<T>
}

declare module './connection.js' {
  interface ServerConnection {
    acquireWorkflowTaskCleanupLease(
      request: WorkflowTaskCleanupLeaseRequest,
      signal: AbortSignal,
    ): Promise<WorkflowTaskCleanupLeaseResult>
    recordWorkflowTaskCleanup(
      operation: WorkflowTaskCleanupOperation,
      signal: AbortSignal,
    ): Promise<WorkflowTaskCleanupOperationResult>
    authorizeTaskSourceAdoption(
      request: WorkflowTaskSourceAdoptionRequest,
      signal: AbortSignal,
    ): Promise<WorkflowTaskSourceAdoptionResult>
    recordTaskSourceAdoption(
      operation: WorkflowTaskSourceAdoption,
      signal: AbortSignal,
    ): Promise<WorkflowTaskSourceAdoptionResult>
    verifyWorkflowWorkspace(verification: WorkspaceVerification, signal: AbortSignal): Promise<Record<string, unknown>>
    allocateFreshRecoveryWorkspace(
      identity: WorkflowTaskExecutionIdentity,
      boundaryFingerprint: string,
      signal: AbortSignal,
    ): Promise<Record<string, unknown>>
  }
}

export function installWorkflowRecoveryMethods(prototype: object): void {
  Object.assign(prototype, {
    acquireWorkflowTaskCleanupLease(
      this: RecoveryConnectionHost,
      request: WorkflowTaskCleanupLeaseRequest,
      signal: AbortSignal,
    ) {
      return this.postRecovery<WorkflowTaskCleanupLeaseResult>('workflow-recovery/cleanup-lease', request, signal)
    },
    recordWorkflowTaskCleanup(
      this: RecoveryConnectionHost,
      operation: WorkflowTaskCleanupOperation,
      signal: AbortSignal,
    ) {
      return this.postRecovery<WorkflowTaskCleanupOperationResult>('workflow-recovery/cleanup', operation, signal)
    },
    authorizeTaskSourceAdoption(
      this: RecoveryConnectionHost,
      request: WorkflowTaskSourceAdoptionRequest,
      signal: AbortSignal,
    ) {
      return this.postRecovery<WorkflowTaskSourceAdoptionResult>(
        'workflow-recovery/adopt-task-source-changes',
        request,
        signal,
      )
    },
    recordTaskSourceAdoption(this: RecoveryConnectionHost, operation: WorkflowTaskSourceAdoption, signal: AbortSignal) {
      return this.postRecovery<WorkflowTaskSourceAdoptionResult>('workflow-recovery/adoption-result', operation, signal)
    },
    verifyWorkflowWorkspace(this: RecoveryConnectionHost, verification: WorkspaceVerification, signal: AbortSignal) {
      return this.postRecovery<Record<string, unknown>>('workflow-recovery/verification', verification, signal)
    },
    allocateFreshRecoveryWorkspace(
      this: RecoveryConnectionHost,
      identity: WorkflowTaskExecutionIdentity,
      boundaryFingerprint: string,
      signal: AbortSignal,
    ) {
      return this.postRecovery<Record<string, unknown>>(
        'workflow-recovery/fresh-workspace',
        { identity, boundaryFingerprint },
        signal,
      )
    },
  })
}
