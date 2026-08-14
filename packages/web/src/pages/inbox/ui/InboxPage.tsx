import { Link } from 'react-router-dom'
import { AlertTriangleIcon, ArchiveIcon, CheckCircle2Icon, MailOpenIcon, PlayIcon } from 'lucide-react'
import {
  NOTIFICATION_KINDS,
  type InboxItem,
  type NotificationKind,
  useArchiveInboxItem,
  useInbox,
  useMarkAllInboxRead,
  useMarkInboxItemRead,
} from '../../../entities/inbox'
import { useProjectPath } from '../../../entities/project'
import { formatRelativeTime } from '../../../shared/lib/relative-time'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { Button } from '@/shared/ui/components/button'

interface KindDescriptor {
  Icon: typeof AlertTriangleIcon
  label: (item: InboxItem) => string
  badge: string
  badgeClass: string
}

const KIND_DESCRIPTORS: Record<NotificationKind, KindDescriptor> = {
  [NOTIFICATION_KINDS.WorkflowFailed]: {
    Icon: AlertTriangleIcon,
    label: (item) => `Issue #${item.issueNumber} workflow failed`,
    badge: 'Failed',
    badgeClass: 'bg-red-100 text-red-700',
  },
  [NOTIFICATION_KINDS.AgentResultUnconfirmed]: {
    Icon: AlertTriangleIcon,
    label: (item) => `Issue #${item.issueNumber} agent result is unconfirmed`,
    badge: 'Blocked',
    badgeClass: 'bg-amber-100 text-amber-700',
  },
  [NOTIFICATION_KINDS.ApprovalRequested]: {
    Icon: MailOpenIcon,
    label: (item) => `Issue #${item.issueNumber} needs approval`,
    badge: 'Approval',
    badgeClass: 'bg-amber-100 text-amber-700',
  },
  [NOTIFICATION_KINDS.IssueStarted]: {
    Icon: PlayIcon,
    label: (item) => `Issue #${item.issueNumber} started`,
    badge: 'Started',
    badgeClass: 'bg-blue-100 text-blue-700',
  },
  [NOTIFICATION_KINDS.IssueCompleted]: {
    Icon: CheckCircle2Icon,
    label: (item) => `Issue #${item.issueNumber} completed`,
    badge: 'Completed',
    badgeClass: 'bg-emerald-100 text-emerald-700',
  },
}

function describeKind(item: InboxItem): KindDescriptor {
  return KIND_DESCRIPTORS[item.notificationKind] ?? KIND_DESCRIPTORS[NOTIFICATION_KINDS.WorkflowFailed]
}

interface InboxItemRowProps {
  item: InboxItem
  onMarkRead: (itemId: string) => void
  onArchive: (itemId: string) => void
  markReadPending: boolean
  archivePending: boolean
}

interface InboxItemMutation {
  mutate: (itemId: string) => void
  isPending: boolean
}

interface InboxBulkMutation {
  mutate: () => void
  isPending: boolean
}

export interface InboxPageData {
  items: InboxItem[] | undefined
  error: unknown
  isError: boolean
  isLoading: boolean
  refetch: () => unknown
  markRead: InboxItemMutation
  markAllRead: InboxBulkMutation
  archive: InboxItemMutation
}

export type InboxPageDataHook = () => InboxPageData

const useDefaultInboxPageData: InboxPageDataHook = () => {
  const inbox = useInbox()
  const markRead = useMarkInboxItemRead()
  const markAllRead = useMarkAllInboxRead()
  const archive = useArchiveInboxItem()
  return {
    items: inbox.data,
    error: inbox.error,
    isError: inbox.isError,
    isLoading: inbox.isLoading,
    refetch: inbox.refetch,
    markRead,
    markAllRead,
    archive,
  }
}

function InboxItemRow({ item, onMarkRead, onArchive, markReadPending, archivePending }: InboxItemRowProps) {
  const toProjectPath = useProjectPath()
  const descriptor = describeKind(item)
  const { Icon } = descriptor

  const baseClasses =
    'block rounded-lg border bg-white p-4 transition-colors hover:shadow-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2'
  const stateClasses = item.isRead
    ? 'border-gray-200 text-muted-foreground'
    : 'border-blue-300 bg-blue-50/40 text-foreground shadow-sm'

  return (
    <div
      className={`${baseClasses} ${stateClasses}`}
      data-testid="inbox-item"
      data-item-id={item.itemId}
      data-read={item.isRead ? 'true' : 'false'}
      data-kind={item.notificationKind}
    >
      <div className="flex items-start gap-3">
        <span
          aria-hidden="true"
          className={`mt-0.5 shrink-0 ${item.isRead ? 'text-muted-foreground' : 'text-foreground'}`}
        >
          <Icon className="size-4" />
        </span>
        <div className="flex-1 min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span
              className={`inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${descriptor.badgeClass}`}
              data-testid="inbox-item-kind"
            >
              {descriptor.badge}
            </span>
            {!item.isRead && (
              <span
                className="inline-flex items-center rounded-full bg-blue-600 px-1.5 py-px text-[10px] font-semibold text-white"
                data-testid="inbox-item-unread-dot"
                aria-label="Unread"
              >
                Unread
              </span>
            )}
            <span className="text-xs text-muted-foreground" data-testid="inbox-item-time">
              {formatRelativeTime(item.createdAt)}
            </span>
          </div>
          <Link
            to={toProjectPath(`/issues/${item.issueNumber}`)}
            className="mt-1 block break-words focus-visible:outline-none focus-visible:underline"
            data-testid="inbox-item-link"
            data-issue-number={item.issueNumber}
          >
            <span className="font-medium text-foreground break-words">{descriptor.label(item)}</span>
            <span className="mt-1 block text-sm text-muted-foreground break-words">
              {item.issueTitle || '(no title)'}
            </span>
          </Link>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1">
          {!item.isRead && (
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => onMarkRead(item.itemId)}
              disabled={markReadPending}
              data-testid="inbox-item-mark-read"
              className="text-blue-600 hover:text-blue-700"
            >
              Mark read
            </Button>
          )}
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => onArchive(item.itemId)}
            disabled={archivePending}
            data-testid="inbox-item-archive"
            className="text-muted-foreground hover:text-foreground"
          >
            <ArchiveIcon className="size-3.5" />
            Archive
          </Button>
        </div>
      </div>
    </div>
  )
}

export function InboxPage({ dataHook = useDefaultInboxPageData }: { dataHook?: InboxPageDataHook } = {}) {
  useDocumentTitle('Inbox — Mohist')

  const { items, error, isError, isLoading, refetch, markRead, markAllRead, archive } = dataHook()

  const list = items ?? []
  const unreadCount = list.filter((item) => !item.isRead).length
  const totalCount = list.length

  return (
    <div className="flex-1 overflow-y-auto">
      <div className="max-w-3xl mx-auto px-4 py-6 md:px-6">
        <div className="flex flex-wrap items-center justify-between gap-2 mb-4">
          <div>
            <h1 className="text-xl font-bold text-foreground" data-testid="inbox-title">
              Inbox
            </h1>
            <p className="text-sm text-muted-foreground" data-testid="inbox-summary">
              {totalCount === 0 ? 'No inbox items yet.' : `${unreadCount} unread of ${totalCount}`}
            </p>
          </div>
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => markAllRead.mutate()}
            disabled={markAllRead.isPending || unreadCount === 0}
            data-testid="inbox-mark-all-read"
          >
            {markAllRead.isPending ? 'Marking...' : 'Mark all read'}
          </Button>
        </div>

        {isLoading ? (
          <div className="flex items-center justify-center py-12">
            <div className="text-muted-foreground">Loading...</div>
          </div>
        ) : isError ? (
          <div
            className="rounded-lg border border-red-200 bg-red-50 py-10 px-4 text-center"
            data-testid="inbox-error-state"
          >
            <div className="text-base font-medium text-red-900">Inbox unavailable</div>
            <div className="mt-1 text-sm text-red-700">
              {error instanceof Error ? error.message : 'Unable to load inbox items.'}
            </div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => refetch()}
              className="mt-4 border-red-300 text-red-800 hover:bg-red-100"
              data-testid="inbox-retry"
            >
              Retry
            </Button>
          </div>
        ) : list.length === 0 ? (
          <div
            className="rounded-lg border border-dashed border-gray-200 bg-gray-50 py-12 text-center"
            data-testid="inbox-empty-state"
          >
            <div className="text-base font-medium text-foreground">No inbox items</div>
            <div className="mt-1 text-sm text-muted-foreground">
              Workflow failures, approvals, and issue lifecycle events will appear here.
            </div>
          </div>
        ) : (
          <div className="space-y-3" data-testid="inbox-list">
            {list.map((item) => (
              <InboxItemRow
                key={item.itemId}
                item={item}
                onMarkRead={(itemId) => markRead.mutate(itemId)}
                onArchive={(itemId) => archive.mutate(itemId)}
                markReadPending={markRead.isPending}
                archivePending={archive.isPending}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
