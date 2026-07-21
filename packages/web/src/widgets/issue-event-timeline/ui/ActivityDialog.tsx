import { useState, type ComponentType } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ActivityIcon } from 'lucide-react'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/shared/ui/components/dialog'
import { useProject } from '../../../entities/project'
import { EventTimelinePanel, type EventTimelinePanelProps } from './EventTimelinePanel'

interface ActivityDialogProps {
  issueNumber: number
  workflowStatus?: string | null
  open?: boolean
  onOpenChange?: (open: boolean) => void
  triggerLabel?: string
  triggerClassName?: string
  triggerTestId?: string
  TimelinePanel?: ComponentType<EventTimelinePanelProps>
}

export function ActivityDialog({
  issueNumber,
  workflowStatus,
  open: controlledOpen,
  onOpenChange,
  triggerLabel = 'Activity',
  triggerClassName,
  triggerTestId = 'activity-entry',
  TimelinePanel = EventTimelinePanel,
}: ActivityDialogProps) {
  const [uncontrolledOpen, setUncontrolledOpen] = useState(false)
  const open = controlledOpen ?? uncontrolledOpen
  const queryClient = useQueryClient()
  const { projectId } = useProject()

  function handleOpenChange(next: boolean) {
    if (next) {
      queryClient.invalidateQueries({ queryKey: ['issue-events', issueNumber, projectId] })
    }
    if (controlledOpen === undefined) setUncontrolledOpen(next)
    onOpenChange?.(next)
  }

  return (
    <>
      <button
        type="button"
        onClick={() => handleOpenChange(true)}
        aria-label={triggerLabel}
        title={triggerLabel}
        data-testid={triggerTestId}
        className={
          triggerClassName ??
          'inline-flex items-center justify-center gap-1.5 min-h-11 min-w-11 sm:min-h-9 sm:min-w-0 rounded-md border border-border bg-background px-2.5 py-1.5 text-sm font-medium text-muted-foreground hover:bg-muted'
        }
      >
        <ActivityIcon className="h-4 w-4" />
        <span>{triggerLabel}</span>
      </button>
      <Dialog open={open} onOpenChange={handleOpenChange}>
        <DialogContent
          id="activity"
          data-testid="activity-dialog-content"
          className="top-0 left-0 w-full h-[100dvh] max-w-full translate-x-0 translate-y-0 rounded-none p-0 gap-0 flex flex-col sm:left-1/2 sm:top-1/2 sm:h-auto sm:max-h-[85vh] sm:max-w-2xl sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-xl sm:p-0"
        >
          <DialogHeader className="border-b border-border px-4 py-3 sm:px-6">
            <DialogTitle>Activity</DialogTitle>
          </DialogHeader>
          <div className="flex-1 min-h-0 overflow-y-auto">
            {open && (
              <TimelinePanel
                issueNumber={issueNumber}
                workflowStatus={workflowStatus}
                enabled={open}
                showHeader={false}
                className="rounded-none border-0 bg-transparent p-4 sm:p-5"
              />
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  )
}
