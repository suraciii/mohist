import type { RunnerStatusRow } from '../../../entities/runner'
import { useRunners } from '../../../entities/runner'
import { Card, CardContent, CardHeader, CardTitle } from '@/shared/ui/components/card'
import { Badge } from '@/shared/ui/components/badge'

const RUNNER_START_HINT = 'npx mohist runner'

function RunnerScopeLabel({ scope }: { scope: RunnerStatusRow['scope'] }) {
  if (scope.type === 'global') {
    return <Badge variant="secondary">global</Badge>
  }
  return (
    <Badge variant="outline">
      {scope.projectName ?? scope.projectId ?? 'project'}
    </Badge>
  )
}

function RunnerStatusBadge({ status }: { status: RunnerStatusRow['status'] }) {
  const variants: Record<RunnerStatusRow['status'], string> = {
    idle: 'bg-green-100 text-green-700',
    busy: 'bg-blue-100 text-blue-700',
    stale: 'bg-amber-100 text-amber-700',
    offline: 'bg-gray-100 text-gray-500',
  }
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ${variants[status]}`}>
      {status}
    </span>
  )
}

function formatHeartbeatAge(lastHeartbeatAt: string, nowMs = Date.now()) {
  const heartbeatMs = new Date(lastHeartbeatAt).getTime()
  if (!Number.isFinite(heartbeatMs)) return 'unknown'

  const ageMs = Math.max(0, nowMs - heartbeatMs)
  const minuteMs = 60 * 1000
  const hourMs = 60 * minuteMs
  const dayMs = 24 * hourMs

  if (ageMs < minuteMs) return 'just now'
  if (ageMs < hourMs) return `${Math.floor(ageMs / minuteMs)}m ago`
  if (ageMs < dayMs) return `${Math.floor(ageMs / hourMs)}h ago`
  return `${Math.floor(ageMs / dayMs)}d ago`
}

function formatHeartbeatTimestamp(lastHeartbeatAt: string) {
  const heartbeatMs = new Date(lastHeartbeatAt).getTime()
  if (!Number.isFinite(heartbeatMs)) return 'unknown'

  return new Date(heartbeatMs).toLocaleTimeString()
}

function heartbeatFreshnessLabel(row: RunnerStatusRow) {
  if (!row.lastHeartbeatAt) return 'heartbeat unknown'
  const kind = row.status === 'stale' || row.status === 'offline' ? row.status : 'fresh'
  return `heartbeat ${kind}: ${formatHeartbeatAge(row.lastHeartbeatAt)}`
}

function RunnerRow({ row }: { row: RunnerStatusRow }) {
  return (
    <div className="flex items-start gap-3 py-3 border-b border-gray-100 last:border-0">
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="font-mono text-xs font-medium text-foreground">{row.id}</span>
          <span className="text-xs text-gray-400">{row.kind}</span>
          <RunnerStatusBadge status={row.status} />
          <RunnerScopeLabel scope={row.scope} />
        </div>
        <div className="flex items-center gap-4 mt-1 text-xs text-gray-500">
          {row.hostname && <span>{row.hostname}</span>}
          <span>{heartbeatFreshnessLabel(row)}</span>
          <span>
            last heartbeat: {row.lastHeartbeatAt ? formatHeartbeatTimestamp(row.lastHeartbeatAt) : 'unknown'}
          </span>
          {row.connectionState && (
            <span className={row.connectionState === 'connected' ? 'text-green-600' : 'text-gray-400'}>
              {row.connectionState}
            </span>
          )}
        </div>
        {row.activeWork && (
          <div className="mt-1 text-xs text-blue-600">
            {row.activeWork.title ?? row.activeWork.workType ?? 'workflow'} ({row.activeWork.workflowRunId})
          </div>
        )}
        {row.capacity && (
          <div className="mt-1 text-xs text-gray-400">
            {row.capacity.usedSlots}/{row.capacity.totalSlots} slots
          </div>
        )}
        {row.capabilities.length > 0 && (
          <div className="mt-1 text-xs text-gray-400">
            capabilities: {row.capabilities.join(', ')}
          </div>
        )}
        {row.coderModels.length > 0 && (
          <div className="mt-1 text-xs text-gray-400">
            {row.coderModelCount} model{row.coderModelCount !== 1 ? 's' : ''}: {row.coderModels.join(', ')}
          </div>
        )}
      </div>
    </div>
  )
}

function RunnerEmptyState() {
  return (
    <div className="py-8 text-center">
      <p className="text-sm text-gray-400 mb-2">No runners connected</p>
      <p className="text-xs text-gray-400">
        Start a runner: <code className="text-xs bg-gray-100 px-1 rounded">{RUNNER_START_HINT}</code>
      </p>
    </div>
  )
}

interface RunnerListProps {
  rows: RunnerStatusRow[]
}

export function RunnerList({ rows }: RunnerListProps) {
  if (rows.length === 0) {
    return <RunnerEmptyState />
  }

  return (
    <div className="divide-y divide-gray-100">
      {rows.map((row) => (
        <RunnerRow key={row.id} row={row} />
      ))}
    </div>
  )
}

export function RunnerListCard() {
  const { data: rows = [], isLoading } = useRunners()

  return (
    <Card>
      <CardHeader className="pb-0">
        <CardTitle>Runners</CardTitle>
      </CardHeader>
      <CardContent className="pt-3">
        {isLoading ? (
          <div className="py-4 text-xs text-gray-400">Loading...</div>
        ) : (
          <RunnerList rows={rows} />
        )}
      </CardContent>
    </Card>
  )
}
