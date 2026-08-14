import { Switch } from '@base-ui/react/switch'
import { useEffect, useRef, useState } from 'react'
import { CardSection } from '@/shared/ui/components/card-section'
import { Label } from '@/shared/ui/components/label'
import {
  NOTIFICATION_KINDS,
  type InboxSubscription,
  type NotificationKind,
  useInboxSubscription,
  useUpdateInboxSubscription,
} from '../../../entities/inbox'
import { getSectionMeta } from '../lib/sections'
import { SettingsSection } from './SettingsSection'

const KIND_LABELS: Record<NotificationKind, string> = {
  [NOTIFICATION_KINDS.WorkflowFailed]: 'Workflow failed',
  [NOTIFICATION_KINDS.AgentResultUnconfirmed]: 'Agent result unconfirmed',
  [NOTIFICATION_KINDS.ApprovalRequested]: 'Approval requested',
  [NOTIFICATION_KINDS.IssueStarted]: 'Issue started',
  [NOTIFICATION_KINDS.IssueCompleted]: 'Issue completed',
}

const KIND_ORDER: NotificationKind[] = [
  NOTIFICATION_KINDS.WorkflowFailed,
  NOTIFICATION_KINDS.AgentResultUnconfirmed,
  NOTIFICATION_KINDS.ApprovalRequested,
  NOTIFICATION_KINDS.IssueStarted,
  NOTIFICATION_KINDS.IssueCompleted,
]

const DEFAULT_SUBSCRIPTION: InboxSubscription = {
  [NOTIFICATION_KINDS.WorkflowFailed]: true,
  [NOTIFICATION_KINDS.AgentResultUnconfirmed]: true,
  [NOTIFICATION_KINDS.ApprovalRequested]: true,
  [NOTIFICATION_KINDS.IssueStarted]: true,
  [NOTIFICATION_KINDS.IssueCompleted]: true,
}

export interface InboxSubscriptionSectionData {
  subscription: InboxSubscription | undefined
  isLoading: boolean
  updateSubscription: (subscription: InboxSubscription) => void
}

export type InboxSubscriptionSectionDataHook = () => InboxSubscriptionSectionData

const useDefaultData: InboxSubscriptionSectionDataHook = () => {
  const { data: subscription, isLoading } = useInboxSubscription()
  const update = useUpdateInboxSubscription()
  return {
    subscription,
    isLoading,
    updateSubscription: (nextSubscription) => update.mutate(nextSubscription),
  }
}

export function InboxSubscriptionSection({
  dataHook = useDefaultData,
}: {
  dataHook?: InboxSubscriptionSectionDataHook
} = {}) {
  const { subscription, isLoading, updateSubscription } = dataHook()
  const [draft, setDraft] = useState<InboxSubscription>(DEFAULT_SUBSCRIPTION)
  const draftRef = useRef<InboxSubscription>(DEFAULT_SUBSCRIPTION)
  const { label: sectionLabel } = getSectionMeta('inbox')

  useEffect(() => {
    if (subscription) {
      draftRef.current = subscription
      setDraft(subscription)
    }
  }, [subscription])

  function handleToggle(kind: NotificationKind, checked: boolean) {
    const next = {
      ...draftRef.current,
      [kind]: checked,
    }
    draftRef.current = next
    setDraft(next)
    updateSubscription(next)
  }

  return (
    <SettingsSection title={sectionLabel}>
      <CardSection title="Workflow updates">
        <p className="mb-4 text-sm text-muted-foreground">Choose which workflow updates create future inbox items.</p>
        {isLoading ? (
          <div className="py-2 text-sm text-muted-foreground">Loading subscription preferences...</div>
        ) : (
          <div className="space-y-3">
            {KIND_ORDER.map((kind) => {
              const checked = draft[kind] ?? true
              return (
                <Label key={kind} className="flex cursor-pointer items-center justify-between gap-4 py-1">
                  <span>{KIND_LABELS[kind]}</span>
                  <Switch.Root
                    checked={checked}
                    onCheckedChange={(nextChecked) => handleToggle(kind, nextChecked)}
                    className="flex h-6 w-10 shrink-0 cursor-pointer items-center rounded-full border border-input bg-gray-200 transition-colors data-[checked]:bg-blue-600 data-[disabled]:cursor-not-allowed data-[disabled]:opacity-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 dark:bg-gray-700 dark:data-[checked]:bg-blue-500"
                    data-testid={`inbox-toggle-${kind}`}
                  >
                    <Switch.Thumb className="block size-4 rounded-full bg-white shadow-sm transition-transform data-[checked]:translate-x-4" />
                  </Switch.Root>
                </Label>
              )
            })}
          </div>
        )}
      </CardSection>
    </SettingsSection>
  )
}
