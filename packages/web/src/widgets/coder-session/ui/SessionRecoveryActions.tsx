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
import type { AgentSessionActivity } from '../../../entities/coder-session'
import { useProject } from '../../../entities/project'

const DISABLED_REASON_TITLE = 'Session is running'
const DISABLED_REASON_BODY =
  'Finish or cancel the session before compacting or resetting.'
const COMPACT_BINDING_TITLE = 'Runtime session unavailable'
const COMPACT_BINDING_BODY = 'Compact requires an available runtime session.'
const PENDING_REASON_TITLE = 'Recovery action in progress'
const PENDING_REASON_BODY =
  'Wait for the current recovery action to finish before starting another one.'
const RESET_CONFIRM_BODY =
  'A new runtime session will start without prior context. Transcript and audit history remain available.'

function DisabledReasonTooltip({
  title,
  body,
  children,
}: {
  title: string
  body: string
  children: React.ReactNode
}) {
  return (
    <Tooltip
      content={
        <div className="space-y-1">
          <div className="font-medium">{title}</div>
          <div>{body}</div>
        </div>
      }
    >
      {children}
    </Tooltip>
  )
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
  runtimeSessionId?: string | null
  runtime?: string | null
  activity?: AgentSessionActivity | string | null | undefined
  status?: string | null | undefined
  recoveryAvailable?: boolean
  onSuccess?: () => void
  onSettled?: () => void
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
  runtimeSessionId,
  runtime,
  activity,
  recoveryAvailable,
  onSuccess,
  onSettled,
  className,
  compactLabel = 'Compact',
  resetLabel = 'Reset',
  bare = false,
  clients = defaultClients,
  genericClients = defaultGenericClients,
}: SessionRecoveryActionsProps) {
  const { projectId } = useProject()
  const active = recoveryAvailable === undefined ? activity !== 'idle' : !recoveryAvailable
  const [resetDialogOpen, setResetDialogOpen] = useState(false)
  const [inlineError, setInlineError] = useState<string | null>(null)
  const [compactIdempotencyKey, setCompactIdempotencyKey] = useState<string | null>(null)
  const [resetIdempotencyKey, setResetIdempotencyKey] = useState<string | null>(null)

  useEffect(() => {
    setInlineError(null)
  }, [activity])

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
    onSettled,
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
    onSettled,
  })

  const anyPending = compactMutation.isPending || resetMutation.isPending
  const hasRuntimeBinding = typeof runtimeSessionId === 'string' && runtimeSessionId.trim().length > 0
    && typeof runtime === 'string' && runtime.trim().length > 0
  const compactDisabledReason = active
    ? { title: DISABLED_REASON_TITLE, body: DISABLED_REASON_BODY }
    : !hasRuntimeBinding
      ? { title: COMPACT_BINDING_TITLE, body: COMPACT_BINDING_BODY }
    : anyPending
      ? { title: PENDING_REASON_TITLE, body: PENDING_REASON_BODY }
      : null
  const resetDisabledReason = active
    ? { title: DISABLED_REASON_TITLE, body: DISABLED_REASON_BODY }
    : anyPending
      ? { title: PENDING_REASON_TITLE, body: PENDING_REASON_BODY }
      : null

  function handleCompact() {
    if (active || !hasRuntimeBinding || anyPending) return
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
      disabled={compactDisabledReason !== null}
      aria-disabled={compactDisabledReason !== null}
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
      disabled={resetDisabledReason !== null}
      aria-disabled={resetDisabledReason !== null}
      data-testid="session-recovery-reset"
      data-active={active ? 'true' : 'false'}
    >
      {resetLabel}
    </Button>
  )

  const content = (
    <>
      <div className="flex items-center gap-2">
        {compactDisabledReason ? (
          <DisabledReasonTooltip {...compactDisabledReason}>{compactButton}</DisabledReasonTooltip>
        ) : (
          compactButton
        )}
        {resetDisabledReason ? (
          <DisabledReasonTooltip {...resetDisabledReason}>{resetButton}</DisabledReasonTooltip>
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
