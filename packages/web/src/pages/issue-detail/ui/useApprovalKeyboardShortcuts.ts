import { useEffect } from 'react'
import type { IssueDecisionAction } from '../model/issueDecisionActions'
import type { IssueDecisionActionController } from '../model/useIssueDecisionActions'

interface UseApprovalKeyboardShortcutsOptions {
  actions: ReadonlyArray<IssueDecisionAction>
  controller: IssueDecisionActionController
  isNarrowViewport: boolean
  onSendBackOpen: () => void
}

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  if (target.isContentEditable || target.getAttribute('role') === 'textbox') return true
  return target instanceof HTMLInputElement
    || target instanceof HTMLTextAreaElement
    || target instanceof HTMLSelectElement
}

export function useApprovalKeyboardShortcuts({
  actions,
  controller,
  isNarrowViewport,
  onSendBackOpen,
}: UseApprovalKeyboardShortcutsOptions) {
  useEffect(() => {
    if (isNarrowViewport) return

    const approve = actions.find((action) => action.kind === 'approve')
    const sendBack = actions.find((action) => action.kind === 'send-back')
    const handleKeyDown = (event: KeyboardEvent) => {
      if (
        event.defaultPrevented
        || event.repeat
        || event.metaKey
        || event.ctrlKey
        || event.altKey
        || event.shiftKey
        || isEditableTarget(event.target)
        || controller.pendingKind !== null
      ) return

      if (event.key.toLowerCase() === 'a' && approve?.enabled) {
        event.preventDefault()
        controller.runAction(approve)
      } else if (event.key.toLowerCase() === 'm' && sendBack?.enabled) {
        event.preventDefault()
        onSendBackOpen()
      }
    }

    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [actions, controller, isNarrowViewport, onSendBackOpen])
}
