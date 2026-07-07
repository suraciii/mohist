import { useEffect, useMemo, useState } from 'react'
import {
  ArchiveIcon,
  ArchiveRestoreIcon,
  Loader2Icon,
  PlusIcon,
  RotateCcwIcon,
  Trash2Icon,
  ZapIcon,
} from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'
import { Textarea } from '@/shared/ui/components/textarea'
import { Badge } from '@/shared/ui/components/badge'
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from '@/shared/ui/components/dialog'
import {
  useAgentSubscriptions,
  useArchiveAgentSubscription,
  useCreateAgentSubscription,
  useDeleteAgentSubscription,
  useRestoreAgentSubscription,
} from '../../../entities/agent'
import type { AgentInfo, AgentSubscriptionDto } from '../../../entities/agent'
import { formatAgentSubscriptionFilter } from '../../../entities/agent'

interface Props {
  agent: Pick<AgentInfo, 'id' | 'status'>
}

interface FormErrors {
  name?: string
  filterType?: string
  responsePrompt?: string
  priority?: string
}

const PRIORITY_DEFAULT_LABEL = 'default'

function previewResponsePrompt(text: string, max: number = 96): string {
  const trimmed = text.trim()
  if (trimmed.length <= max) return trimmed
  return `${trimmed.slice(0, max - 1)}…`
}

export function SubscriptionsSection({ agent }: Props) {
  const isArchived = agent.status === 'archived'
  const { data: subscriptions = [], isLoading } = useAgentSubscriptions(agent.id)
  const createMutation = useCreateAgentSubscription(agent.id)
  const archiveMutation = useArchiveAgentSubscription(agent.id)
  const restoreMutation = useRestoreAgentSubscription(agent.id)
  const deleteMutation = useDeleteAgentSubscription(agent.id)

  const [createOpen, setCreateOpen] = useState(false)
  const [name, setName] = useState('')
  const [filterType, setFilterType] = useState('')
  const [filterSource, setFilterSource] = useState('')
  const [filterSubject, setFilterSubject] = useState('')
  const [responsePrompt, setResponsePrompt] = useState('')
  const [priorityText, setPriorityText] = useState('')
  const [formErrors, setFormErrors] = useState<FormErrors>({})
  const [pendingDeleteId, setPendingDeleteId] = useState<string | null>(null)

  useEffect(() => {
    if (!createOpen) return
    setName('')
    setFilterType('')
    setFilterSource('')
    setFilterSubject('')
    setResponsePrompt('')
    setPriorityText('')
    setFormErrors({})
  }, [createOpen])

  const sorted = useMemo(
    () =>
      [...subscriptions].sort((a, b) => {
        if (a.status !== b.status) return a.status === 'active' ? -1 : 1
        return a.name.localeCompare(b.name)
      }),
    [subscriptions],
  )

  function validate(): FormErrors {
    const errors: FormErrors = {}
    if (!name.trim()) errors.name = 'Name is required'
    if (!filterType.trim()) errors.filterType = 'Filter type is required'
    if (!responsePrompt.trim()) errors.responsePrompt = 'Response prompt is required'
    if (priorityText.trim()) {
      const parsed = Number(priorityText)
      if (!Number.isFinite(parsed) || !Number.isInteger(parsed)) {
        errors.priority = 'Priority must be an integer'
      }
    }
    return errors
  }

  function handleCreate() {
    const validation = validate()
    setFormErrors(validation)
    if (Object.keys(validation).length > 0) return

    const trimmedPriority = priorityText.trim()
    const parsedPriority = trimmedPriority ? Number(trimmedPriority) : null

    createMutation.mutate(
      {
        name: name.trim(),
        filter: {
          type: filterType.trim(),
          source: filterSource.trim() || null,
          subject: filterSubject.trim() || null,
        },
        responsePrompt: responsePrompt,
        priority: trimmedPriority ? (parsedPriority as number) : null,
      },
      {
        onSuccess: () => {
          setCreateOpen(false)
        },
      },
    )
  }

  function handleArchive(subscription: AgentSubscriptionDto) {
    archiveMutation.mutate({ subscriptionId: subscription.id })
  }

  function handleRestore(subscription: AgentSubscriptionDto) {
    restoreMutation.mutate({ subscriptionId: subscription.id })
  }

  function handleDeleteConfirm() {
    if (!pendingDeleteId) return
    deleteMutation.mutate(
      { subscriptionId: pendingDeleteId },
      {
        onSuccess: () => {
          setPendingDeleteId(null)
        },
      },
    )
  }

  const isCreating = createMutation.isPending
  const isArchiving = archiveMutation.isPending
  const isRestoring = restoreMutation.isPending
  const isDeleting = deleteMutation.isPending

  return (
    <div className="rounded-lg border border-border bg-card p-4" data-testid="agent-subscriptions-section">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-sm font-medium text-foreground">Subscriptions</h3>
        <Button
          size="sm"
          variant="outline"
          onClick={() => setCreateOpen(true)}
          data-testid="agent-subscriptions-create"
          disabled={isArchived}
          aria-label="Create subscription"
        >
          <PlusIcon />
          Create
        </Button>
      </div>

      {isArchived && (
        <div
          data-testid="agent-subscriptions-archived-notice"
          className="rounded-md bg-muted/60 border border-border px-3 py-2 text-xs text-muted-foreground mb-3"
        >
          Archived agents cannot receive new subscriptions. Their existing subscriptions are also
          inactive.
        </div>
      )}

      {isLoading ? (
        <div data-testid="agent-subscriptions-loading" className="text-xs text-muted-foreground py-4 text-center">
          Loading subscriptions...
        </div>
      ) : sorted.length === 0 ? (
        <div
          data-testid="agent-subscriptions-empty"
          className="text-xs text-muted-foreground py-4 text-center"
        >
          No subscriptions yet. Create one to make this agent react to events.
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
                  <div className="flex items-center gap-2 flex-wrap">
                    <span className="text-sm font-medium text-foreground truncate">{subscription.name}</span>
                    <SubscriptionStatusBadge status={subscription.status} />
                  </div>
                  <div
                    className="text-[11px] font-mono text-muted-foreground mt-0.5"
                    data-testid={`agent-subscription-row-${subscription.id}-filter`}
                  >
                    {formatAgentSubscriptionFilter(subscription.filter)}
                  </div>
                </div>
                <div className="text-right text-[11px] text-muted-foreground shrink-0">
                  <div className="flex items-center gap-1 justify-end">
                    <ZapIcon className="size-3" />
                    <span data-testid={`agent-subscription-row-${subscription.id}-priority`}>
                      {subscription.priority == null ? PRIORITY_DEFAULT_LABEL : `priority ${subscription.priority}`}
                    </span>
                  </div>
                </div>
              </div>
              <div
                className="mt-1 text-xs text-muted-foreground italic"
                data-testid={`agent-subscription-row-${subscription.id}-prompt-preview`}
              >
                {previewResponsePrompt(subscription.responsePrompt) || (
                  <span className="opacity-50">No response prompt</span>
                )}
              </div>
              <div className="mt-2 flex items-center gap-2 justify-end">
                {subscription.status === 'active' ? (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => handleArchive(subscription)}
                    disabled={isArchiving || isArchived}
                    data-testid={`agent-subscription-archive-${subscription.id}`}
                  >
                    {isArchiving ? <Loader2Icon className="size-4 animate-spin" /> : <ArchiveIcon />}
                    Archive
                  </Button>
                ) : (
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => handleRestore(subscription)}
                    disabled={isRestoring}
                    data-testid={`agent-subscription-restore-${subscription.id}`}
                  >
                    {isRestoring ? <Loader2Icon className="size-4 animate-spin" /> : <ArchiveRestoreIcon />}
                    Restore
                  </Button>
                )}
                <Button
                  size="sm"
                  variant="outline"
                  className="text-red-600 hover:text-red-700 hover:bg-red-50"
                  onClick={() => setPendingDeleteId(subscription.id)}
                  disabled={isDeleting}
                  data-testid={`agent-subscription-delete-${subscription.id}`}
                >
                  {isDeleting ? <Loader2Icon className="size-4 animate-spin" /> : <Trash2Icon />}
                  Delete
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}

      <Dialog open={createOpen} onOpenChange={(open) => { if (!isCreating) setCreateOpen(open) }}>
        <DialogContent className="sm:max-w-lg" data-testid="agent-subscriptions-create-dialog">
          <DialogHeader>
            <DialogTitle>Create Subscription</DialogTitle>
            <DialogDescription>
              Wire this agent to react to events by providing a filter expression and the response
              prompt to inject when the agent auto-launches.
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-3">
            <div className="space-y-1.5">
              <Label htmlFor="subscription-name">Name *</Label>
              <Input
                id="subscription-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. fallback-approver"
                data-testid="subscription-create-name"
                className={formErrors.name ? 'border-red-500' : ''}
              />
              {formErrors.name && (
                <p data-testid="subscription-create-name-error" className="text-xs text-red-500">
                  {formErrors.name}
                </p>
              )}
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="subscription-filter-type">Filter type *</Label>
              <Input
                id="subscription-filter-type"
                value={filterType}
                onChange={(e) => setFilterType(e.target.value)}
                placeholder="com.mohist.workflow.stage.*"
                data-testid="subscription-create-filter-type"
                className={formErrors.filterType ? 'border-red-500' : ''}
              />
              {formErrors.filterType && (
                <p data-testid="subscription-create-filter-type-error" className="text-xs text-red-500">
                  {formErrors.filterType}
                </p>
              )}
              <p className="text-[10px] text-muted-foreground">
                Supports <code>|</code> (or), <code>*</code> (all), and <code>prefix.*</code>{' '}
                (sub-domain).
              </p>
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
              <div className="space-y-1.5">
                <Label htmlFor="subscription-filter-source">Filter source</Label>
                <Input
                  id="subscription-filter-source"
                  value={filterSource}
                  onChange={(e) => setFilterSource(e.target.value)}
                  placeholder="(optional)"
                  data-testid="subscription-create-filter-source"
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="subscription-filter-subject">Filter subject</Label>
                <Input
                  id="subscription-filter-subject"
                  value={filterSubject}
                  onChange={(e) => setFilterSubject(e.target.value)}
                  placeholder="(optional)"
                  data-testid="subscription-create-filter-subject"
                />
              </div>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="subscription-response-prompt">Response prompt *</Label>
              <Textarea
                id="subscription-response-prompt"
                rows={4}
                value={responsePrompt}
                onChange={(e) => setResponsePrompt(e.target.value)}
                placeholder="Approve the workflow if the proposal is clear. Variables: {{workflow_run_id}}, {{stage}}, {{event_type}}."
                data-testid="subscription-create-response-prompt"
                className={formErrors.responsePrompt ? 'border-red-500' : ''}
              />
              {formErrors.responsePrompt && (
                <p data-testid="subscription-create-response-prompt-error" className="text-xs text-red-500">
                  {formErrors.responsePrompt}
                </p>
              )}
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="subscription-priority">Priority</Label>
              <Input
                id="subscription-priority"
                value={priorityText}
                onChange={(e) => setPriorityText(e.target.value)}
                placeholder="default"
                data-testid="subscription-create-priority"
                className={formErrors.priority ? 'border-red-500' : ''}
              />
              {formErrors.priority && (
                <p data-testid="subscription-create-priority-error" className="text-xs text-red-500">
                  {formErrors.priority}
                </p>
              )}
              <p className="text-[10px] text-muted-foreground">
                Higher priority takes precedence. Leave blank to fall back to default (0).
              </p>
            </div>
          </div>

          <div className="flex justify-end gap-2 pt-2 border-t">
            <Button
              variant="outline"
              onClick={() => setCreateOpen(false)}
              disabled={isCreating}
              data-testid="subscription-create-cancel"
            >
              Cancel
            </Button>
            <Button
              onClick={handleCreate}
              disabled={isCreating || isArchived}
              data-testid="subscription-create-submit"
            >
              {isCreating && <Loader2Icon className="size-4 animate-spin" />}
              {isArchived ? 'Unavailable for archived agents' : 'Create Subscription'}
            </Button>
          </div>
        </DialogContent>
      </Dialog>

      <Dialog open={pendingDeleteId !== null} onOpenChange={(open) => { if (!open) setPendingDeleteId(null) }}>
        <DialogContent className="sm:max-w-sm" data-testid="agent-subscription-delete-confirm-dialog">
          <DialogHeader>
            <DialogTitle>Delete Subscription</DialogTitle>
            <DialogDescription>
              This permanently removes the subscription and stops any future events from triggering
              it. Already-running sessions are unaffected.
            </DialogDescription>
          </DialogHeader>
          <div className="flex justify-end gap-2 pt-2">
            <Button
              variant="outline"
              onClick={() => setPendingDeleteId(null)}
              disabled={isDeleting}
              data-testid="agent-subscription-delete-cancel"
            >
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={handleDeleteConfirm}
              disabled={isDeleting}
              data-testid="agent-subscription-delete-confirm"
            >
              {isDeleting && <Loader2Icon className="size-4 animate-spin" />}
              Delete
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

function SubscriptionStatusBadge({ status }: { status: AgentSubscriptionDto['status'] }) {
  if (status === 'active') {
    return (
      <Badge variant="default" className="text-[10px] px-1.5 py-0 h-4">
        active
      </Badge>
    )
  }
  return (
    <Badge
      variant="outline"
      className="text-[10px] px-1.5 py-0 h-4 text-muted-foreground border-muted-foreground/30"
    >
      <RotateCcwIcon className="size-3 mr-0.5" />
      archived
    </Badge>
  )
}
