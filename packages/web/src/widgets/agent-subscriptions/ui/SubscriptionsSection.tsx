import { useEffect, useMemo, useState } from 'react'
import { Edit3Icon, Loader2Icon, PlusIcon, Trash2Icon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'
import { Textarea } from '@/shared/ui/components/textarea'
import { Badge } from '@/shared/ui/components/badge'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/shared/ui/components/dialog'
import {
  useAgentSubscriptions,
  useCreateAgentSubscription,
  useDeleteAgentSubscription,
  useUpdateAgentSubscription,
} from '../../../entities/agent'
import type {
  AgentInfo,
  AgentSubscriptionCreateRequest,
  AgentSubscriptionDto,
  AgentSubscriptionListDto,
  AgentSubscriptionUpdateRequest,
} from '../../../entities/agent'
import { createIdempotencyKey } from '@/shared/lib/idempotency-key'

interface Props {
  agent: Pick<AgentInfo, 'id' | 'status'>
  operationsHook?: SubscriptionOperationsHook
}

export interface SubscriptionOperations {
  subscriptionsQuery: {
    data?: AgentSubscriptionListDto
    isLoading: boolean
    isError: boolean
    error: unknown
  }
  createMutation: SubscriptionMutation<AgentSubscriptionCreateRequest>
  updateMutation: SubscriptionMutation<{ subscriptionId: string; data: AgentSubscriptionUpdateRequest }>
  deleteMutation: SubscriptionMutation<{ subscriptionId: string }>
}

interface SubscriptionMutation<T> {
  mutate: (variables: T, options?: { onSuccess?: (...args: any[]) => void }) => void
  isPending: boolean
}

export type SubscriptionOperationsHook = (agentRef: string) => SubscriptionOperations

const useDefaultOperations: SubscriptionOperationsHook = (agentRef) => ({
  subscriptionsQuery: useAgentSubscriptions(agentRef),
  createMutation: useCreateAgentSubscription(agentRef),
  updateMutation: useUpdateAgentSubscription(agentRef),
  deleteMutation: useDeleteAgentSubscription(agentRef),
})

interface FormErrors {
  name?: string
  match?: string
  responsePrompt?: string
}

function previewResponsePrompt(text: string, max = 96): string {
  const trimmed = text.trim()
  return trimmed.length <= max ? trimmed : `${trimmed.slice(0, max - 1)}...`
}

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message ? error.message : 'Subscriptions could not be loaded.'
}

function stateLabel(data: AgentSubscriptionListDto | undefined): string {
  if (!data) return ''
  if (data.state === 'empty') return 'No subscriptions configured'
  if (data.state === 'unconfigured') return 'Agent needs setup'
  if (data.state === 'unavailable') return 'Subscription service unavailable'
  if (data.state === 'no_connection') return 'No connection installed'
  return 'Subscriptions configured'
}

export function SubscriptionsSection({ agent, operationsHook = useDefaultOperations }: Props) {
  const isArchived = agent.status === 'archived'
  const { subscriptionsQuery, createMutation, updateMutation, deleteMutation } = operationsHook(agent.id)
  const { data, isLoading, isError, error } = subscriptionsQuery
  const subscriptions = data?.subscriptions ?? []
  const sorted = useMemo(
    () => [...subscriptions].sort((a, b) => a.position - b.position || a.name.localeCompare(b.name)),
    [subscriptions],
  )

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editing, setEditing] = useState<AgentSubscriptionDto | null>(null)
  const [pendingDeleteId, setPendingDeleteId] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [match, setMatch] = useState('')
  const [responsePrompt, setResponsePrompt] = useState('')
  const [continueAfterMatch, setContinueAfterMatch] = useState(false)
  const [formErrors, setFormErrors] = useState<FormErrors>({})
  const [pendingCreateKey, setPendingCreateKey] = useState<string | null>(null)

  useEffect(() => {
    if (!dialogOpen) return
    setName(editing?.name ?? '')
    setMatch(editing?.match ?? '')
    setResponsePrompt(editing?.responsePrompt ?? '')
    setContinueAfterMatch(editing?.continue ?? false)
    setFormErrors({})
  }, [dialogOpen, editing])

  function openCreate() {
    setEditing(null)
    setDialogOpen(true)
  }

  function openEdit(subscription: AgentSubscriptionDto) {
    if (isArchived) return
    setEditing(subscription)
    setDialogOpen(true)
  }

  function validate(): FormErrors {
    const errors: FormErrors = {}
    if (!name.trim()) errors.name = 'Name is required'
    if (!match.trim()) errors.match = 'Match expression is required'
    if (!responsePrompt.trim()) errors.responsePrompt = 'Response prompt is required'
    return errors
  }

  function save() {
    const errors = validate()
    setFormErrors(errors)
    if (Object.keys(errors).length > 0) return

    if (editing) {
      const data: AgentSubscriptionUpdateRequest = {
        name: name.trim(),
        match: match.trim(),
        responsePrompt,
        continue: continueAfterMatch,
      }
      updateMutation.mutate(
        { subscriptionId: editing.id, data },
        { onSuccess: () => setDialogOpen(false) },
      )
      return
    }

    const idempotencyKey = pendingCreateKey ?? createIdempotencyKey()
    setPendingCreateKey(idempotencyKey)
    createMutation.mutate(
      {
        name: name.trim(),
        match: match.trim(),
        responsePrompt,
        continue: continueAfterMatch,
        idempotencyKey,
      },
      { onSuccess: () => { setPendingCreateKey(null); setDialogOpen(false) } },
    )
  }

  function confirmDelete() {
    if (!pendingDeleteId) return
    deleteMutation.mutate(
      { subscriptionId: pendingDeleteId },
      { onSuccess: () => setPendingDeleteId(null) },
    )
  }

  const isSaving = createMutation.isPending || updateMutation.isPending
  const isDeleting = deleteMutation.isPending

  return (
    <div className="rounded-lg border border-border bg-card p-4" data-testid="agent-subscriptions-section">
      <div className="mb-3 flex items-center justify-between gap-3">
        <div>
          <h3 className="text-sm font-medium text-foreground">Subscriptions</h3>
          {data && (
            <p className="mt-1 text-xs text-muted-foreground" data-testid="agent-subscriptions-state">
              {stateLabel(data)} · {data.connection}
            </p>
          )}
        </div>
        <Button
          size="sm"
          variant="outline"
          onClick={openCreate}
          data-testid="agent-subscriptions-create"
          disabled={isArchived || isSaving}
          aria-label="Create subscription"
        >
          <PlusIcon />
          Create
        </Button>
      </div>

      {isArchived && (
        <div data-testid="agent-subscriptions-archived-notice" className="mb-3 rounded-md border border-border bg-muted/60 px-3 py-2 text-xs text-muted-foreground">
          Archived agents keep their subscriptions for inspection, but cannot create or update them.
        </div>
      )}

      {isLoading ? (
        <div data-testid="agent-subscriptions-loading" className="py-4 text-center text-xs text-muted-foreground">
          Loading subscriptions...
        </div>
      ) : isError ? (
        <div data-testid="agent-subscriptions-error" className="py-4 text-center text-xs text-destructive">
          {errorMessage(error)}
        </div>
      ) : sorted.length === 0 ? (
        <div data-testid="agent-subscriptions-empty" className="py-4 text-center text-xs text-muted-foreground">
          {stateLabel(data) || 'No subscriptions configured.'}
        </div>
      ) : (
        <ul className="space-y-2" data-testid="agent-subscriptions-list">
          {sorted.map((subscription) => (
            <li
              key={subscription.id}
              data-testid={`agent-subscription-row-${subscription.id}`}
              data-subscription-status={subscription.status}
              className="rounded-md border border-border bg-background/60 px-3 py-2"
            >
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="truncate text-sm font-medium text-foreground">{subscription.name}</span>
                    <Badge variant={subscription.status === 'active' ? 'secondary' : 'outline'}>{subscription.status}</Badge>
                  </div>
                  <div className="mt-1 break-words text-[11px] font-mono text-muted-foreground" data-testid={`agent-subscription-row-${subscription.id}-match`}>
                    {subscription.match}
                  </div>
                  <div className="mt-1 text-xs italic text-muted-foreground" data-testid={`agent-subscription-row-${subscription.id}-prompt-preview`}>
                    {previewResponsePrompt(subscription.responsePrompt) || 'No response prompt'}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-1">
                  <Button
                    size="icon"
                    variant="ghost"
                    onClick={() => openEdit(subscription)}
                    disabled={isArchived || isSaving}
                    data-testid={`agent-subscription-edit-${subscription.id}`}
                    aria-label={`Edit subscription ${subscription.name}`}
                  >
                    <Edit3Icon />
                  </Button>
                  <Button
                    size="icon"
                    variant="ghost"
                    className="text-red-600 hover:bg-red-50 hover:text-red-700"
                    onClick={() => setPendingDeleteId(subscription.id)}
                    disabled={isDeleting}
                    data-testid={`agent-subscription-delete-${subscription.id}`}
                    aria-label={`Delete subscription ${subscription.name}`}
                  >
                    {isDeleting ? <Loader2Icon className="animate-spin" /> : <Trash2Icon />}
                  </Button>
                </div>
              </div>
            </li>
          ))}
        </ul>
      )}

      <Dialog open={dialogOpen} onOpenChange={(open) => {
        if (isSaving) return
        if (!open) setPendingCreateKey(null)
        setDialogOpen(open)
      }}>
        <DialogContent className="sm:max-w-lg" data-testid="agent-subscriptions-edit-dialog">
          <DialogHeader>
            <DialogTitle>{editing ? 'Edit Subscription' : 'Create Subscription'}</DialogTitle>
            <DialogDescription>Configure the event expression and response prompt used by this Agent.</DialogDescription>
          </DialogHeader>
          <div className="space-y-3">
            <div className="space-y-1.5">
              <Label htmlFor="subscription-name">Name *</Label>
              <Input id="subscription-name" value={name} onChange={(event) => setName(event.target.value)} data-testid="subscription-create-name" />
              {formErrors.name && <p data-testid="subscription-create-name-error" className="text-xs text-red-500">{formErrors.name}</p>}
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="subscription-match">Match expression *</Label>
              <Input id="subscription-match" value={match} onChange={(event) => setMatch(event.target.value)} placeholder={'event.type == "com.example.event"'} data-testid="subscription-create-match" />
              {formErrors.match && <p data-testid="subscription-create-match-error" className="text-xs text-red-500">{formErrors.match}</p>}
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="subscription-response-prompt">Response prompt *</Label>
              <Textarea id="subscription-response-prompt" value={responsePrompt} onChange={(event) => setResponsePrompt(event.target.value)} data-testid="subscription-create-response-prompt" />
              {formErrors.responsePrompt && <p data-testid="subscription-create-response-prompt-error" className="text-xs text-red-500">{formErrors.responsePrompt}</p>}
            </div>
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              <input type="checkbox" checked={continueAfterMatch} onChange={(event) => setContinueAfterMatch(event.target.checked)} data-testid="subscription-create-continue" />
              Continue evaluating later rules
            </label>
            {pendingCreateKey && !editing && (
              <p className="break-all text-xs text-muted-foreground" data-testid="subscription-create-idempotency-key">
                Idempotency key: {pendingCreateKey}. Retry this create with the same key if the response is lost.
              </p>
            )}
            <div className="flex justify-end gap-2">
              <Button variant="outline" onClick={() => setDialogOpen(false)} disabled={isSaving}>Cancel</Button>
              <Button onClick={save} disabled={isSaving} data-testid="subscription-create-submit">
                {isSaving && <Loader2Icon className="animate-spin" />}
                {editing ? 'Save' : 'Create'}
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>

      <Dialog open={pendingDeleteId !== null} onOpenChange={(open) => { if (!open && !isDeleting) setPendingDeleteId(null) }}>
        <DialogContent data-testid="agent-subscription-delete-confirm-dialog">
          <DialogHeader>
            <DialogTitle>Delete subscription?</DialogTitle>
            <DialogDescription>This removes the routing rule. The action is permanent.</DialogDescription>
          </DialogHeader>
          <div className="flex justify-end gap-2">
            <Button variant="outline" onClick={() => setPendingDeleteId(null)} disabled={isDeleting} data-testid="agent-subscription-delete-cancel">Cancel</Button>
            <Button variant="destructive" onClick={confirmDelete} disabled={isDeleting} data-testid="agent-subscription-delete-confirm">Delete</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}
