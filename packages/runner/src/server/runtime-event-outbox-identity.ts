import type { RuntimeEventRecord } from './runtime-event-outbox-ports.js'

interface SequenceKey {
  readonly family:
    | 'workflow-session'
    | 'workflow-cleanup'
    | 'session-followup'
    | 'generic-followup'
    | 'binding-reconcile'
  readonly projectId?: string
  readonly workflowRunId?: string
  readonly sessionName?: string
  readonly sessionId?: string
  readonly runtimeSessionId?: string
  readonly sessionTurnId?: string
  readonly cleanupOperationId?: string
  readonly execution?: WorkflowRuntimeEventExecutionIdentity | null
}

export interface WorkflowRuntimeEventExecutionIdentity {
  readonly runnerId: string
  readonly agentSessionId: string
  readonly taskRunId: string
  readonly workId: string
  readonly inputDeliveryId: string
  readonly agentTurnId: string | null
  readonly runtime: string
  readonly runtimeSessionId: string
}

export function runtimeEventDeliveryKey(record: RuntimeEventRecord): string {
  return sequenceKeyLabel(sequenceKey(record))
}

export function runtimeEventSchedulingKey(record: RuntimeEventRecord): string {
  if (record.producerFamily !== 'workflow-session' && record.producerFamily !== 'workflow-cleanup')
    return runtimeEventDeliveryKey(record)
  if (record.target.kind !== 'workflow') throw new Error('workflow scheduling family requires workflow target')
  return JSON.stringify({
    family: 'workflow-session',
    projectId: record.target.projectId,
    workflowRunId: record.target.workflowRunId,
    sessionName: record.target.sessionName,
  })
}

export function isWorkflowSessionBoundary(record: RuntimeEventRecord): boolean {
  return (
    (record.producerFamily === 'workflow-session' && record.event.type === 'session.input') ||
    (record.producerFamily === 'workflow-cleanup' && record.event.type === 'session.cleanup')
  )
}

export function workflowExecutionIdentity(record: RuntimeEventRecord): WorkflowRuntimeEventExecutionIdentity | null {
  if (record.producerFamily !== 'workflow-session' || record.target.kind !== 'workflow') return null
  const work = record.work
  if (
    !work ||
    !nonEmpty(work.workId) ||
    !nonEmpty(work.taskRunId) ||
    !nonEmpty(work.runnerId) ||
    !nonEmpty(work.agentSessionId) ||
    !nonEmpty(work.inputDeliveryId) ||
    !nonEmpty(record.runtime) ||
    !nonEmpty(record.runtimeSessionId)
  ) {
    throw new Error('workflow-session execution record requires its complete immutable execution identity')
  }
  if (work.agentTurnId !== undefined && work.agentTurnId !== null && !nonEmpty(work.agentTurnId))
    throw new Error('workflow-session execution record has an invalid Agent turn identity')
  return {
    runnerId: work.runnerId,
    agentSessionId: work.agentSessionId,
    taskRunId: work.taskRunId,
    workId: work.workId,
    inputDeliveryId: work.inputDeliveryId,
    agentTurnId: work.agentTurnId ?? null,
    runtime: record.runtime,
    runtimeSessionId: record.runtimeSessionId,
  }
}

function sequenceKey(record: RuntimeEventRecord): SequenceKey {
  if (record.producerFamily === 'workflow-session') {
    if (record.target.kind !== 'workflow') throw new Error('workflow-session family requires workflow target')
    return {
      family: 'workflow-session',
      projectId: record.target.projectId,
      workflowRunId: record.target.workflowRunId,
      sessionName: record.target.sessionName,
      execution: workflowExecutionIdentity(record),
    }
  }
  if (record.producerFamily === 'workflow-cleanup') {
    if (record.target.kind !== 'workflow') throw new Error('workflow-cleanup family requires workflow target')
    return {
      family: 'workflow-cleanup',
      projectId: record.target.projectId,
      workflowRunId: record.target.workflowRunId,
      sessionName: record.target.sessionName,
      cleanupOperationId: record.id,
    }
  }
  if (record.producerFamily === 'binding-reconcile') {
    if (record.target.kind !== 'session') throw new Error('binding-reconcile family requires session target')
    return {
      family: 'binding-reconcile',
      sessionId: record.target.sessionId,
      runtimeSessionId: record.runtimeSessionId,
    }
  }
  if (record.producerFamily === 'session-followup') {
    if (record.target.kind !== 'session') throw new Error('session-followup family requires Session target')
    if (!nonEmpty(record.sessionTurnId))
      throw new Error('session-followup record requires its immutable Agent turn identity')
    return {
      family: 'session-followup',
      sessionId: record.target.sessionId,
      runtimeSessionId: record.runtimeSessionId,
      sessionTurnId: record.sessionTurnId,
    }
  }
  if (record.target.kind !== 'generic') throw new Error('generic-followup family requires generic target')
  return {
    family: 'generic-followup',
    projectId: record.target.projectId,
    sessionId: record.target.sessionId,
  }
}

function sequenceKeyLabel(key: SequenceKey): string {
  if (key.family === 'workflow-session') {
    return JSON.stringify({
      family: key.family,
      projectId: key.projectId,
      workflowRunId: key.workflowRunId,
      sessionName: key.sessionName,
      execution: key.execution ?? null,
    })
  }
  if (key.family === 'workflow-cleanup') {
    return JSON.stringify({
      family: key.family,
      projectId: key.projectId,
      workflowRunId: key.workflowRunId,
      sessionName: key.sessionName,
      cleanupOperationId: key.cleanupOperationId,
    })
  }
  if (key.family === 'binding-reconcile') {
    return `binding-reconcile:${key.sessionId}:${key.runtimeSessionId}`
  }
  if (key.family === 'session-followup') {
    return `session-followup:${key.sessionId}:${key.runtimeSessionId}:${key.sessionTurnId}`
  }
  return `generic-followup:${key.projectId}:${key.sessionId}`
}

function nonEmpty(value: string | null | undefined): value is string {
  return typeof value === 'string' && value.length > 0
}
