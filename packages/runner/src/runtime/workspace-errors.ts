import { NETWORK_COMMAND_TIMEOUT_MS } from '../actions/git.js'
import type { CommandResult } from '../system/process.js'
import { createCredentialMaskerFromEnvironment } from './task-log.js'
import type { IssueWorkspaceMarker } from './workspace-identity.js'

export class WorkspaceMissingError extends Error {
  readonly kind = 'workspace-missing'
  constructor(
    message: string,
    readonly workspacePath?: string,
    readonly cause?: unknown,
  ) {
    super(message)
    this.name = 'WorkspaceMissingError'
  }
}

export class WorkspaceCorruptError extends Error {
  readonly kind = 'workspace-corrupt'
  constructor(
    message: string,
    readonly workspacePath?: string,
    readonly cause?: unknown,
  ) {
    super(message)
    this.name = 'WorkspaceCorruptError'
  }
}

export class WorkspaceIdentityMismatchError extends Error {
  readonly kind = 'workspace-identity-mismatch'
  constructor(
    message: string,
    readonly workspacePath?: string,
    readonly expected?: IssueWorkspaceMarker,
    readonly actual?: Partial<IssueWorkspaceMarker>,
    readonly cause?: unknown,
    readonly originDiagnostic?: WorkspaceOriginDiagnostic,
  ) {
    super(message)
    this.name = 'WorkspaceIdentityMismatchError'
  }
}

export interface WorkspaceOriginDiagnostic {
  kind: 'probe-failed' | 'value-mismatch'
  exitCode: number
  diagnostic: string
}

export class WorkspaceBranchMismatchError extends Error {
  readonly kind = 'branch-invariant-violation'
  constructor(
    message: string,
    readonly workspacePath: string,
    readonly expectedBranch: string,
    readonly observedBranch: string | null,
    readonly observedRef: string | null = null,
    readonly detail?: string,
  ) {
    super(message)
    this.name = 'WorkspaceBranchMismatchError'
  }
}

export interface WorkspaceNetworkTimeoutStep {
  name: string
  command: string
  exitCode: number
  output: string
  status: 'timeout'
  timeoutMs?: number
}

export class WorkspaceNetworkTimeoutError extends Error {
  readonly kind = 'workspace-network-timeout'
  constructor(
    message: string,
    readonly step: WorkspaceNetworkTimeoutStep,
  ) {
    super(message)
    this.name = 'WorkspaceNetworkTimeoutError'
  }
}

export function workspaceNetworkTimeout(
  name: string,
  command: string,
  result: CommandResult,
  managedPath?: string,
  displayPath?: string,
): WorkspaceNetworkTimeoutError {
  const output = sanitizeWorkspaceDiagnostic(
    [result.stdout.trim(), result.stderr.trim()].filter(Boolean).join('\n'),
    managedPath,
    displayPath,
  )
  const visibleCommand = redactWorkspaceDiagnostic(command)
  return new WorkspaceNetworkTimeoutError(
    `Workspace preparation network command timed out: ${name} (${visibleCommand}) after ${(result.timeoutMs ?? NETWORK_COMMAND_TIMEOUT_MS) / 1000}s`,
    {
      name,
      command: visibleCommand,
      exitCode: result.exitCode,
      output,
      status: 'timeout',
      timeoutMs: result.timeoutMs,
    },
  )
}

export function sanitizeWorkspaceDiagnostic(value: string, managedPath?: string, displayPath?: string): string {
  let sanitized = value
  if (managedPath && displayPath && sanitized.includes(managedPath))
    sanitized = sanitized.split(managedPath).join(displayPath)
  return redactWorkspaceDiagnostic(sanitized)
}

export function redactWorkspaceDiagnostic(value: string): string {
  return createCredentialMaskerFromEnvironment().mask(value)
}
