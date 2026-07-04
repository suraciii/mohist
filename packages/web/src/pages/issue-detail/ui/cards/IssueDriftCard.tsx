import { CardSection } from '@/shared/ui/components/card-section'
import type { BaseDriftInfo } from '../../../../entities/issue'

export interface IssueDriftCardProps {
  drift: BaseDriftInfo
  unframed?: boolean
}

export function IssueDriftCard({ drift, unframed = false }: IssueDriftCardProps) {
  const content = (
    <div className="space-y-1.5 text-xs">
        {drift.decision && (
          <div className="flex justify-between">
            <span className="text-muted-foreground">Rebase decision:</span>
            <span className={`font-medium ${drift.decision === 'needs-attention' ? 'text-danger' : drift.decision === 'defer' ? 'text-warning' : 'text-warning'}`}>
              {drift.decision === 'needs-attention' ? 'Needs Attention' :
               drift.decision === 'defer' ? 'Deferred' :
               drift.decision === 'suggest' ? 'Suggested' :
               drift.decision === 'enqueue' ? 'Enqueued' : drift.decision}
            </span>
          </div>
        )}
        {drift.deferReason && (
          <div className="flex justify-between">
            <span className="text-muted-foreground">Defer reason:</span>
            <span className="text-warning text-right">
              {drift.deferReason === 'agent-running' ? 'Agent running' :
               drift.deferReason === 'task-running' ? 'Task running' :
               drift.deferReason === 'waiting-for-task-boundary' ? 'Waiting for task boundary' :
               drift.deferReason === 'rebase-already-pending' ? 'Rebase already pending' :
               drift.deferReason}
            </span>
          </div>
        )}
        {drift.safeWindow !== null && (
          <div className="flex justify-between">
            <span className="text-muted-foreground">Safe window:</span>
            <span className={drift.safeWindow ? 'text-success' : 'text-foreground/80'}>
              {drift.safeWindow ? 'Yes' : 'No'}
            </span>
          </div>
        )}
        {drift.observedBaseSha && drift.currentBaseSha && (
          <div className="flex justify-between">
            <span className="text-muted-foreground">Base:</span>
            <span className="font-mono text-foreground/80">
              {drift.observedBaseSha.slice(0, 7)} → {drift.currentBaseSha.slice(0, 7)}
            </span>
          </div>
        )}
        {drift.nextAction && (
          <div className="mt-2 pt-2 border-t border-warning-border text-warning">
            {drift.nextAction}
          </div>
        )}
        {drift.conflicts && drift.conflicts.length > 0 && (
          <div className="mt-2 pt-2 border-t border-danger-border">
            <span className="font-medium text-danger">Conflicts: </span>
            {drift.conflicts.map((f) => (
              <span key={f} className="font-mono text-danger ml-1">{f}</span>
            ))}
          </div>
        )}
    </div>
  )
  if (unframed) return content
  return <CardSection title="Base Drift Detected" tone="amber">{content}</CardSection>
}
