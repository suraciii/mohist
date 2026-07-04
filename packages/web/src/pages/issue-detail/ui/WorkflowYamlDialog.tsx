import { useState } from 'react'
import { useWorkflowYaml } from '../../../entities/issue'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/shared/ui/components/dialog'

export function WorkflowYamlDialog({
  workflowRunId,
  isArchived,
}: {
  workflowRunId: string
  isArchived: boolean
}) {
  const [open, setOpen] = useState(false)
  const { data, isLoading } = useWorkflowYaml(workflowRunId, open)
  const heading = isArchived ? 'Workflow run YAML' : 'Active run YAML'
  const description = isArchived
    ? 'Rendered runtime output of the preserved workflow run. The workflow is no longer active; this is the historical record.'
    : 'Rendered runtime output of the active workflow run, not the issue\u0027s workflow profile configuration.'

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        data-testid="active-run-yaml-trigger"
        data-yaml-mode={isArchived ? 'archived' : 'active'}
        className="w-full text-left rounded-lg border border-border bg-card p-3 hover:bg-muted transition-colors"
      >
        <div className="flex items-center justify-between">
          <span className="text-sm text-card-foreground">{heading}</span>
          <span className="text-xs text-info">View</span>
        </div>
        <p className="mt-1 text-xs text-muted-foreground">{description}</p>
      </button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="sm:max-w-4xl max-h-[80vh] overflow-hidden flex flex-col p-0">
          <DialogHeader>
            <DialogTitle>{heading}</DialogTitle>
            <p className="text-xs text-muted-foreground pt-1">{description}</p>
          </DialogHeader>
          <div className="flex-1 overflow-auto px-4 pb-4">
            {isLoading ? (
              <div className="space-y-2">
                {[1, 2, 3, 4, 5].map((i) => (
                  <div key={i} className="h-4 bg-muted rounded animate-pulse" />
                ))}
              </div>
            ) : data?.yaml ? (
              <pre className="text-xs font-mono leading-relaxed text-card-foreground whitespace-pre-wrap break-all bg-muted rounded-md p-4 border">
                {data.yaml}
              </pre>
            ) : (
              <p className="text-sm text-muted-foreground">No workflow YAML available.</p>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </>
  )
}
