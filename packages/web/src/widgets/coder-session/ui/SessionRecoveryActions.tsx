import { useEffect, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError } from '@/shared/api/client'
import { Button } from '@/shared/ui/components/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { Tooltip } from '@/shared/ui/components/tooltip'
import { cn } from '@/shared/lib/utils'
import {
  compactGenericSession,
  compactSession,
  resetGenericSession,
  resetSession,
} from '../../../entities/coder-session'
import { useProject } from '../../../entities/project'

const DISABLED_REASON_TITLE = 'Session is running'
const DISABLED_REASON_BODY =
  'Finish or cancel the session before compacting or resetting.'
const ACTIVE_STATUSES = new Set(['running', 'active', 'live'])
const RESET_CONFIRM_BODY =
  'A new runtime session will start without prior context. Transcript and audit history remain available.'

function DisabledReasonTooltip({ children }: { children: React.ReactNode }) {
  return (
    <Tooltip
      content={
        <div className="space-y-1">
          <div className="font-medium">{DISABLED_REASON_TITLE}</div>
          <div>{DISABLED_REASON_BODY}</div>
        </div>
      }
    >
      {children}
    </Tooltip>
  )
}

function isSessionActive(status: string | null | undefined): boolean {
  if (!status) return false
  return ACTIVE_STATUSES.has(status)
}

function resolveErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 409) {
      return err.message && err.message.trim().length > 0
        ? err.message
        : 'Cannot perform this action while session is active.'
    }
    if (err.status === 404) {
      return 'Session not found.'
    }
    return err.message || `Request failed with status ${err.status}.`
  }
  if (err instanceof Error) return err.message
  return 'An unexpected error occurred.'
}

export interface SessionRecoveryActionsProps {
  issueNumber: number
  sessionName: string
  genericSessionId?: string
  status: string | null | undefined
  recoveryAvailable?: boolean
  onSuccess?: () => void
  className?: string
  compactLabel?: string
  resetLabel?: string
  /**
   * When true, the action buttons are rendered with no wrapper chrome so
   * the parent component can place them in its own layout. Defaults to
   * false (the component renders its own container).
   */
  bare?: boolean
  clients?: SessionRecoveryActionsClients
  genericClients?: GenericSessionRecoveryActionsClients
}

export interface SessionRecoveryActionsClients {
  compact: typeof compactSession
  reset: typeof resetSession
}

export interface GenericSessionRecoveryActionsClients {
  compact: typeof compactGenericSession
  reset: typeof resetGenericSession
}

const defaultClients: SessionRecoveryActionsClients = {
  compact: compactSession,
  reset: resetSession,
}

const defaultGenericClients: GenericSessionRecoveryActionsClients = {
  compact: compactGenericSession,
  reset: resetGenericSession,
}

export function SessionRecoveryActions({
  issueNumber,
  sessionName,
  genericSessionId,
  status,
  recoveryAvailable,
  onSuccess,
  className,
  compactLabel = 'Compact',
  resetLabel = 'Reset',
  bare = false,
  clients = defaultClients,
  genericClients = defaultGenericClients,
}: SessionRecoveryActionsProps) {
  const { projectId } = useProject()
  const active = recoveryAvailable === undefined ? isSessionActive(status) : !recoveryAvailable
  const [resetDialogOpen, setResetDialogOpen] = useState(false)
  const [inlineError, setInlineError] = useState<string | null>(null)
  const [compactIdempotencyKey, setCompactIdempotencyKey] = useState<string | null>(null)
  const [resetIdempotencyKey, setResetIdempotencyKey] = useState<string | null>(null)

  useEffect(() => {
    setInlineError(null)
  }, [status])

  const compactMutation = useMutation({
    mutationFn: (idempotencyKey: string) => {
      if (!projectId) {
        return Promise.reject(new ApiError('Project is required', 400))
      }
      return genericSessionId
        ? genericClients.compact(genericSessionId, projectId, idempotencyKey)
        : clients.compact(issueNumber, sessionName, projectId, idempotencyKey)
    },
    onSuccess: () => {
      setCompactIdempotencyKey(null)
      setInlineError(null)
      onSuccess?.()
    },
    onError: (err) => {
      setInlineError(resolveErrorMessage(err))
    },
  })

  const resetMutation = useMutation({
    mutationFn: (idempotencyKey: string) => {
      if (!projectId) {
        return Promise.reject(new ApiError('Project is required', 400))
      }
      return genericSessionId
        ? genericClients.reset(genericSessionId, projectId, idempotencyKey)
        : clients.reset(issueNumber, sessionName, projectId, idempotencyKey)
    },
    onSuccess: () => {
      setResetIdempotencyKey(null)
      setResetDialogOpen(false)
      setInlineError(null)
      onSuccess?.()
    },
    onError: (err) => {
      setInlineError(resolveErrorMessage(err))
    },
  })

  const anyPending = compactMutation.isPending || resetMutation.isPending

  function handleCompact() {
    if (active || anyPending) return
    const idempotencyKey = compactIdempotencyKey ?? crypto.randomUUID()
    setCompactIdempotencyKey(idempotencyKey)
    compactMutation.mutate(idempotencyKey)
  }

  function openResetDialog() {
    if (active || anyPending) return
    setInlineError(null)
    setResetDialogOpen(true)
  }

  function handleResetCancel(open: boolean) {
    if (resetMutation.isPending) return
    if (!open) {
      setResetDialogOpen(false)
      if (resetMutation.isError) {
        setInlineError(null)
        resetMutation.reset()
      }
    }
  }

  function handleResetConfirm() {
    if (resetMutation.isPending) return
    const idempotencyKey = resetIdempotencyKey ?? crypto.randomUUID()
    setResetIdempotencyKey(idempotencyKey)
    resetMutation.mutate(idempotencyKey)
  }

  const compactButton = (
    <Button
      type="button"
      variant="outline"
      size="sm"
      onClick={handleCompact}
      disabled={active || anyPending}
      aria-disabled={active}
      data-testid="session-recovery-compact"
      data-active={active ? 'true' : 'false'}
    >
      {compactMutation.isPending ? 'Compacting…' : compactLabel}
    </Button>
  )

  const resetButton = (
    <Button
      type="button"
      variant="destructive"
      size="sm"
      onClick={openResetDialog}
      disabled={active || anyPending}
      aria-disabled={active}
      data-testid="session-recovery-reset"
      data-active={active ? 'true' : 'false'}
    >
      {resetLabel}
    </Button>
  )

  const content = (
    <>
      <div className="flex items-center gap-2">
        {active ? (
          <DisabledReasonTooltip>{compactButton}</DisabledReasonTooltip>
        ) : (
          compactButton
        )}
        {active ? (
          <DisabledReasonTooltip>{resetButton}</DisabledReasonTooltip>
        ) : (
          resetButton
        )}
      </div>

      {inlineError && (
        <div
          role="alert"
          aria-live="polite"
          data-testid="session-recovery-error"
          data-tone="danger"
          className="basis-full min-w-0 max-w-full w-full break-words mt-2 rounded-md border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger"
        >
          {inlineError}
        </div>
      )}

      <Dialog
        open={resetDialogOpen}
        onOpenChange={handleResetCancel}
      >
        <DialogContent data-testid="session-recovery-reset-dialog">
          <DialogHeader>
            <DialogTitle>Reset session?</DialogTitle>
            <DialogDescription>{RESET_CONFIRM_BODY}</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => handleResetCancel(false)}
              disabled={resetMutation.isPending}
              data-testid="session-recovery-reset-cancel"
            >
              Cancel
            </Button>
            <Button
              type="button"
              variant="destructive"
              size="sm"
              onClick={handleResetConfirm}
              disabled={resetMutation.isPending}
              data-testid="session-recovery-reset-confirm"
            >
              {resetMutation.isPending ? 'Resetting…' : 'Reset Session'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  )

  if (bare) return content

  return (
    <div
      data-testid="session-recovery-actions"
      data-active={active ? 'true' : 'false'}
      className={cn('flex flex-col', className)}
    >
      {content}
    </div>
  )
}
