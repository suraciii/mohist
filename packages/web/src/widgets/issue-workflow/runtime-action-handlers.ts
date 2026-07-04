import type { RuntimeActionKind, RuntimeDecision } from './model/derive-runtime-decision'
import type { RuntimeDecisionSurfaceMutations } from './ui/RuntimeDecisionSurface'

export interface StopConsequenceCopy {
  title: string
  body: string
}

export function getStopConsequenceCopy(stopRecoverable: boolean | null): StopConsequenceCopy {
  if (stopRecoverable) {
    return {
      title: 'Stop (recoverable)',
      body: 'Stop will preserve progress so this workflow can be resumed later.',
    }
  }
  return {
    title: 'Stop (irreversible)',
    body: 'Stop is irreversible for this workflow run; progress cannot be resumed.',
  }
}

export interface InvokeActionCallbacks {
  onSendBackSuccess?: () => void
}

export interface InvokeActionParams {
  decision: RuntimeDecision
  mutations: RuntimeDecisionSurfaceMutations
  sendBackBody?: string
  callbacks?: InvokeActionCallbacks
}

export function invokeAction(
  kind: RuntimeActionKind,
  params: InvokeActionParams,
): void {
  const { decision, mutations, sendBackBody, callbacks } = params

  if (kind === 'approve') {
    mutations.approveMutation.mutate()
    return
  }

  if (kind === 'send-back') {
    if (!decision.approvalStage) return
    const body = (sendBackBody ?? '').trim()
    if (!body) return
    const variables = { stage: decision.approvalStage, body }
    if (callbacks?.onSendBackSuccess) {
      mutations.sendBackMutation.mutate(variables, { onSuccess: callbacks.onSendBackSuccess })
      return
    }
    mutations.sendBackMutation.mutate(variables)
    return
  }

  if (kind === 'retry') {
    mutations.retryMutation.mutate()
    return
  }

  if (kind === 'resume') {
    mutations.resumeMutation.mutate()
    return
  }

  if (kind === 'rerun') {
    mutations.rerunMutation.mutate()
    return
  }

  if (kind === 'stop') {
    if (decision.stopRecoverable) {
      mutations.forceStopMutation.mutate()
    } else {
      mutations.stopMutation.mutate()
    }
    return
  }

  if (kind === 'start') {
    mutations.startMutation.mutate()
    return
  }
}