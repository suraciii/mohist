import { useCallback, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import type { IssueDetailMutations } from './useIssueDetailMutations'
import type { IssueDecisionAction, IssueDecisionActionKind } from './issueDecisionActions'

export interface IssueDecisionActionController {
  pendingKind: IssueDecisionActionKind | null
  error: Error | null
  stopConfirming: boolean
  stopConfirmTitle: string
  stopConfirmBody: string
  openStopConfirm(): void
  closeStopConfirm(): void
  runAction(action: IssueDecisionAction, options?: { sendBackBody?: string }): void
  sendBackBodyValid(body: string, approvalStage?: string | null): boolean
}

export interface IssueDecisionControllerContext {
  mutations: IssueDetailMutations
  stopRecoverable: boolean | null
  approvalStage: string | null
  stopCopy: { title: string; body: string }
  navigate: (path: string) => void
  stopConfirmOpen: boolean
  setStopConfirmOpen(open: boolean): void
}

export interface IssueDecisionControllerOptions {
  mutations: IssueDetailMutations
  stopRecoverable: boolean | null
  approvalStage: string | null
  getStopConsequenceCopy: (stopRecoverable: boolean | null) => { title: string; body: string }
}

export function getStopConsequenceCopy(stopRecoverable: boolean | null): { title: string; body: string } {
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

function pickPendingKind(mutations: IssueDetailMutations): IssueDecisionActionKind | null {
  if (mutations.approveMutation.isPending) return 'approve'
  if (mutations.sendBackMutation.isPending) return 'send-back'
  if (mutations.retryMutation.isPending) return 'retry'
  if (mutations.resumeMutation.isPending) return 'resume'
  if (mutations.rerunMutation.isPending) return 'rerun'
  if (mutations.forceStopMutation.isPending || mutations.stopMutation.isPending) return 'stop'
  if (mutations.startMutation.isPending) return 'start'
  if (mutations.markReadyMutation.isPending) return 'mark-ready'
  if (mutations.closeMutation.isPending) return 'close'
  if (mutations.markDoneMutation.isPending) return 'mark-as-done'
  return null
}

function firstMutationError(mutations: IssueDetailMutations): { kind: IssueDecisionActionKind; error: Error } | null {
  const pairs: Array<{ kind: IssueDecisionActionKind; error: Error | null }> = [
    { kind: 'approve', error: mutations.approveMutation.error },
    { kind: 'send-back', error: mutations.sendBackMutation.error },
    { kind: 'retry', error: mutations.retryMutation.error },
    { kind: 'resume', error: mutations.resumeMutation.error },
    { kind: 'rerun', error: mutations.rerunMutation.error },
    { kind: 'stop', error: mutations.forceStopMutation.error ?? mutations.stopMutation.error },
    { kind: 'start', error: mutations.startMutation.error },
    { kind: 'mark-ready', error: mutations.markReadyMutation.error },
    { kind: 'close', error: mutations.closeMutation.error },
    { kind: 'mark-as-done', error: mutations.markDoneMutation.error },
  ]
  for (const pair of pairs) {
    if (pair.error) return { kind: pair.kind, error: pair.error }
  }
  return null
}

function collectError(
  mutations: IssueDetailMutations,
  pendingKind: IssueDecisionActionKind | null,
): Error | null {
  if (pendingKind) {
    switch (pendingKind) {
      case 'approve': return mutations.approveMutation.error
      case 'send-back': return mutations.sendBackMutation.error
      case 'retry': return mutations.retryMutation.error
      case 'resume': return mutations.resumeMutation.error
      case 'rerun': return mutations.rerunMutation.error
      case 'stop':
        return mutations.forceStopMutation.error
          ? mutations.forceStopMutation.error
          : mutations.stopMutation.error
      case 'start': return mutations.startMutation.error
      case 'mark-ready': return mutations.markReadyMutation.error
      case 'close': return mutations.closeMutation.error
      case 'mark-as-done': return mutations.markDoneMutation.error
      default: return null
    }
  }
  return firstMutationError(mutations)?.error ?? null
}

export function runControllerAction(
  ctx: IssueDecisionControllerContext,
  action: IssueDecisionAction,
  options?: { sendBackBody?: string },
): void {
  if (pickPendingKind(ctx.mutations)) return

  switch (action.kind) {
    case 'approve':
      ctx.mutations.approveMutation.mutate()
      return
    case 'send-back': {
      const body = (options?.sendBackBody ?? '').trim()
      if (!body || !ctx.approvalStage) return
      ctx.mutations.sendBackMutation.mutate({ stage: ctx.approvalStage, body }, {
        onSuccess: () => ctx.setStopConfirmOpen(false),
      })
      return
    }
    case 'retry':
      ctx.mutations.retryMutation.mutate()
      return
    case 'resume':
      ctx.mutations.resumeMutation.mutate()
      return
    case 'rerun':
      ctx.mutations.rerunMutation.mutate()
      return
    case 'stop':
      if (ctx.stopRecoverable) {
        ctx.mutations.forceStopMutation.mutate()
      } else {
        ctx.mutations.stopMutation.mutate()
      }
      return
    case 'start':
      ctx.mutations.startMutation.mutate()
      return
    case 'mark-ready':
      ctx.mutations.markReadyMutation.mutate()
      return
    case 'close':
      ctx.mutations.closeMutation.mutate()
      return
    case 'mark-as-done':
      ctx.mutations.markDoneMutation.mutate()
      return
    case 'ask-agent':
    case 'view-transcript':
      if (action.to) ctx.navigate(action.to)
      return
  }
}

export function buildIssueDecisionActionController(
  ctx: Omit<IssueDecisionControllerContext, 'stopCopy' | 'stopConfirmOpen' | 'setStopConfirmOpen'>,
): Pick<IssueDecisionActionController, 'pendingKind' | 'error' | 'stopConfirmTitle' | 'stopConfirmBody'> {
  return {
    pendingKind: pickPendingKind(ctx.mutations),
    error: collectError(ctx.mutations, pickPendingKind(ctx.mutations)),
    stopConfirmTitle: ctx.stopRecoverable === true ? 'Stop (recoverable)' : 'Stop (irreversible)',
    stopConfirmBody: ctx.stopRecoverable === true
      ? 'Stop will preserve progress so this workflow can be resumed later.'
      : 'Stop is irreversible for this workflow run; progress cannot be resumed.',
  }
}

export function useIssueDecisionActionController({
  mutations,
  stopRecoverable,
  approvalStage,
  getStopConsequenceCopy,
}: IssueDecisionControllerOptions): IssueDecisionActionController {
  const navigate = useNavigate()
  const [stopConfirmOpen, setStopConfirmOpen] = useState(false)

  const pendingKind = pickPendingKind(mutations)
  const error = collectError(mutations, pendingKind)
  const stopCopy = getStopConsequenceCopy(stopRecoverable)
  const stopConfirming = stopConfirmOpen && pendingKind !== 'stop'

  const runAction = useCallback((action: IssueDecisionAction, options?: { sendBackBody?: string }) => {
    runControllerAction({
      mutations,
      stopRecoverable,
      approvalStage,
      navigate,
      stopCopy,
      stopConfirmOpen,
      setStopConfirmOpen,
    }, action, options)
  }, [mutations, stopRecoverable, approvalStage, navigate, stopCopy, stopConfirmOpen])

  const openStopConfirm = useCallback(() => {
    if (!pendingKind) setStopConfirmOpen(true)
  }, [pendingKind])
  const closeStopConfirm = useCallback(() => {
    if (mutations.stopMutation.isPending || mutations.forceStopMutation.isPending) return
    setStopConfirmOpen(false)
  }, [mutations.stopMutation.isPending, mutations.forceStopMutation.isPending])

  return {
    pendingKind,
    error,
    stopConfirming,
    stopConfirmTitle: stopCopy.title,
    stopConfirmBody: stopCopy.body,
    openStopConfirm,
    closeStopConfirm,
    runAction,
    sendBackBodyValid: (body, stageOverride) => {
      const stage = stageOverride ?? approvalStage
      return !!stage && body.trim().length > 0
    },
  }
}
