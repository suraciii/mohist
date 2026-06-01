import { useState, useEffect } from 'react'
import { useLogLevel, useSetLogLevel, useSystemInfo, useSystemUpdate, useSystemUpdateStatus } from '../../../entities/settings'
import { Button } from '@/shared/ui/components/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'

const LOG_LEVELS = ['DEBUG', 'INFO', 'WARN', 'ERROR'] as const
const DEFAULT_LOG_LEVEL = 'INFO'

function StatusBadge({ status }: { status: string | null | undefined }) {
  const running = status === 'active' || status === 'running'
  if (running) {
    return (
      <span className="inline-flex items-center gap-1 text-xs font-medium text-green-700">
        <span className="w-1.5 h-1.5 rounded-full bg-green-500" />
        Running
      </span>
    )
  }
  return (
    <span className="inline-flex items-center gap-1 text-xs font-medium text-muted-foreground">
      <span className="w-1.5 h-1.5 rounded-full bg-muted-foreground/70" />
      Stopped
    </span>
  )
}

function InfoRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between py-2 border-b last:border-b-0">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span className="text-xs text-foreground font-mono">{children}</span>
    </div>
  )
}

function RefreshIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M4 2a1 1 0 011 1v2.101a7.002 7.002 0 0111.601 2.566 1 1 0 11-1.885.666A5.002 5.002 0 005.999 7H9a1 1 0 010 2H4a1 1 0 01-1-1V3a1 1 0 011-1zm.008 9.057a1 1 0 011.276.61l5.954 5.954a.75.75 0 11-1.06 1.06l-5.954-5.954A1 1 0 0112.008 11z" clipRule="evenodd" />
    </svg>
  )
}

function SpinnerIcon({ className }: { className?: string }) {
  return (
    <svg className={`animate-spin ${className || ''}`} viewBox="0 0 20 20" fill="none">
      <circle className="opacity-25" cx="10" cy="10" r="8" stroke="currentColor" strokeWidth="3" />
      <path className="opacity-75" fill="currentColor" d="M4 10a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.817 3 7.938l3-2.647z" />
    </svg>
  )
}

async function checkHealth(): Promise<boolean> {
  try {
    const res = await fetch('/api/health')
    return res.ok
  } catch {
    return false
  }
}

function shortHash(value: string | null | undefined) {
  if (!value) return 'unknown'
  return value.slice(0, 8)
}

function formatValue(value: string | null | undefined) {
  return value && value.length > 0 ? value : '—'
}

function formatTimestamp(value: string | null | undefined) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

export function SystemSettingsSection() {
  const { data: logLevelData, isLoading: logLevelLoading } = useLogLevel()
  const setLogLevel = useSetLogLevel()
  const { data: systemInfo, isLoading: infoLoading, isError: infoError, error: infoErrorValue, refetch: refetchInfo } = useSystemInfo()
  const systemUpdate = useSystemUpdate()
  const [trackingUpdate, setTrackingUpdate] = useState(false)
  const { data: updateStatusEnvelope, refetch: refetchUpdateStatus } = useSystemUpdateStatus(true)
  const [reconnectState, setReconnectState] = useState<string | null>(null)
  const updateStatus = updateStatusEnvelope?.job ?? null

  const [currentLevel, setCurrentLevel] = useState(DEFAULT_LOG_LEVEL)
  const [saving, setSaving] = useState(false)
  const [logError, setLogError] = useState<string | null>(null)

  useEffect(() => {
    if (logLevelData?.level) {
      setCurrentLevel(logLevelData.level)
    }
  }, [logLevelData])

  const handleLogLevelChange = async (newLevel: string) => {
    const previousLevel = currentLevel
    setCurrentLevel(newLevel)
    setLogError(null)
    setSaving(true)

    try {
      await setLogLevel.mutateAsync(newLevel)
    } catch (err) {
      setCurrentLevel(previousLevel)
      setLogError(err instanceof Error ? err.message : 'Failed to update log level')
    } finally {
      setSaving(false)
    }
  }

  useEffect(() => {
    if (updateStatus?.status === 'succeeded') {
      setTrackingUpdate(false)
      setReconnectState(null)
      refetchInfo()
      return
    }

    if (updateStatus?.status === 'failed') {
      setTrackingUpdate(false)
      setReconnectState(null)
      refetchInfo()
      return
    }

    if (updateStatus?.status === 'waiting-for-reconnect') {
      setReconnectState('Waiting for reconnect')
      let cancelled = false
      const poll = window.setInterval(async () => {
        const ok = await checkHealth()
        if (!cancelled && ok) {
          setReconnectState('Refreshing runtime info')
          await refetchInfo()
          await refetchUpdateStatus()
        }
      }, 2000)

      return () => {
        cancelled = true
        clearInterval(poll)
      }
    }
  }, [updateStatus?.status, refetchInfo, refetchUpdateStatus])

  useEffect(() => {
    if (!updateStatus || !systemInfo) return
    if (updateStatus.status === 'waiting-for-reconnect' && systemInfo.running.gitHash && systemInfo.running.gitHash === updateStatus.sourceHead) {
      setReconnectState('Ready')
    }
  }, [updateStatus, systemInfo])

  const isLoading = logLevelLoading || infoLoading
  const sourceHead = systemInfo?.source.head ?? null
  const gitHash = systemInfo?.running.gitHash ?? null
  const updateReady = updateStatus?.status === 'succeeded'
    || (updateStatus?.status === 'waiting-for-reconnect' && !!gitHash && gitHash === updateStatus.sourceHead)
  const persistedUpdateActive = updateStatus?.status === 'running' || updateStatus?.status === 'waiting-for-reconnect'
  const showUpdateButton = systemInfo?.install.mode === 'local-source'
    && systemInfo.update.available
    && systemInfo.update.status === 'update-available'
    && !persistedUpdateActive
    && !trackingUpdate
  const showProgress = trackingUpdate || persistedUpdateActive || updateReady || reconnectState === 'Ready'
  const progressLabel = updateReady ? 'Ready' : reconnectState ?? updateStatus?.stage ?? null
  const updateMessage = updateStatus?.reason ?? systemInfo?.update.reason ?? null
  const recentUpdateLogs = updateStatus?.logs?.slice(-5).reverse() ?? []

  const handleUpdate = async () => {
    if (!systemInfo) return
    if (systemInfo.source.dirty) return
    setReconnectState(null)
    await systemUpdate.mutateAsync()
    setTrackingUpdate(true)
  }

  if (isLoading) {
    return (
      <div className="space-y-8">
        <h3 className="text-sm font-medium text-foreground">System</h3>
        <div className="space-y-1.5">
          <div className="h-4 w-24 bg-muted rounded animate-pulse" />
          <div className="h-9 w-full bg-muted rounded-md animate-pulse" />
        </div>
        <div className="space-y-2">
          {[1, 2, 3, 4, 5].map((i) => (
            <div key={i} className="h-8 bg-muted rounded animate-pulse" />
          ))}
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-8">
      <div>
        <h3 className="text-sm font-medium text-foreground">System</h3>
        <p className="text-xs text-muted-foreground mt-1">Logging, runtime identity, and local-source update status.</p>
      </div>

      <div className="space-y-3">
        <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Logging</h4>

        <div className="space-y-1.5">
          <label className="block text-xs font-medium text-foreground/80">Log Level</label>
          <Select value={currentLevel} onValueChange={(value) => value && handleLogLevelChange(value)} disabled={saving}>
            <SelectTrigger className="w-full max-w-xs">
              <SelectValue placeholder="Select log level" />
            </SelectTrigger>
            <SelectContent>
              {LOG_LEVELS.map((level) => (
                <SelectItem key={level} value={level}>
                  {level}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          {saving && <p className="text-xs text-muted-foreground/70">Saving...</p>}
        </div>

        {logError && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {logError}
          </div>
        )}

        <div className="space-y-1">
          <span className="block text-xs font-medium text-foreground/80">Log Path</span>
          <p className="text-xs text-muted-foreground font-mono">~/.mohist/logs/</p>
        </div>
      </div>

      <hr className="border" />

      <div className="space-y-3">
        <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Server Runtime</h4>

        {infoError || !systemInfo ? (
          <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700">
            Failed to load server runtime: {infoErrorValue instanceof Error ? infoErrorValue.message : 'runtime info unavailable'}
          </div>
        ) : (
        <div className="rounded-md border px-4 py-1">
          <InfoRow label="Running version">
            {formatValue(systemInfo.running.version)}
          </InfoRow>
          <InfoRow label="Running git hash">
            <span title={gitHash ?? undefined}>{shortHash(gitHash)}</span>
          </InfoRow>
          <InfoRow label="Started at">
            {formatTimestamp(systemInfo.running.startedAt)}
          </InfoRow>
          <InfoRow label="Source path">
            {formatValue(systemInfo.source.path)}
          </InfoRow>
          <InfoRow label="Source branch">
            {formatValue(systemInfo.source.branch)}
          </InfoRow>
          <InfoRow label="Source HEAD">
            <span title={sourceHead ?? undefined}>{sourceHead ? `${shortHash(sourceHead)} (${sourceHead})` : 'unknown'}</span>
          </InfoRow>
          <InfoRow label="Source dirty state">
            {systemInfo.source.dirty ? 'dirty' : 'clean'}
          </InfoRow>
          <InfoRow label="Install mode">
            {formatValue(systemInfo.install.mode)}
          </InfoRow>
          <InfoRow label="Install detail">
            {formatValue(systemInfo.install.reason)}
          </InfoRow>
          <InfoRow label="Service manager">
            {formatValue(systemInfo.install.serviceManager)}
          </InfoRow>
          <InfoRow label="Server unit">
            {formatValue(systemInfo.install.serverUnit)}
          </InfoRow>
          <InfoRow label="Runner unit">
            {formatValue(systemInfo.install.runnerUnit)}
          </InfoRow>
          <InfoRow label="Update status">
            {formatValue(systemInfo.update.status)}
          </InfoRow>
          <InfoRow label="Server service status">
            <span className="inline-flex items-center gap-2">
              {formatValue(systemInfo.services.server)} <StatusBadge status={systemInfo.services.server} />
            </span>
          </InfoRow>
          <InfoRow label="Runner service status">
            <span className="inline-flex items-center gap-2">
              {formatValue(systemInfo.services.runner)} <StatusBadge status={systemInfo.services.runner} />
            </span>
          </InfoRow>
          <InfoRow label="Database">
            {systemInfo.paths.db ?? '—'}
          </InfoRow>
          <InfoRow label="Config">
            {systemInfo.paths.config ?? '—'}
          </InfoRow>
          <InfoRow label="Opencode">
            {systemInfo.paths.opencode ?? '—'}
          </InfoRow>
          <InfoRow label="Logs">
            {systemInfo.paths.logs ?? '—'}
          </InfoRow>
        </div>
        )}

        {updateMessage && (
          <div className="rounded-md bg-muted px-3 py-2 text-xs text-muted-foreground">
            {updateMessage}
          </div>
        )}

        {systemInfo?.source.dirty && (
          <div className="rounded-md bg-amber-50 border border-amber-200 px-3 py-2 text-xs text-amber-700">
            Local source has uncommitted changes. Update is disabled until the tree is clean.
          </div>
        )}

        {systemInfo && systemInfo.install.mode !== 'local-source' && (
          <div className="rounded-md bg-muted px-3 py-2 text-xs text-muted-foreground">
            Web update is unsupported for this deployment.
          </div>
        )}

        {(showUpdateButton || showProgress) && (
          <div className="pt-2">
            {showProgress ? (
              <div className="space-y-3">
                <span className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-amber-600 bg-amber-50 rounded-md">
                  {!updateReady && <SpinnerIcon className="h-4 w-4" />}
                  {progressLabel ?? 'Waiting for reconnect'}
                </span>
                {(updateStatus?.sourcePath || updateStatus?.serverUnit || updateStatus?.runnerUnit) && (
                  <div className="rounded-md border px-3 py-2 text-xs text-muted-foreground">
                    {updateStatus.sourcePath && <div>Source: <span className="font-mono">{updateStatus.sourcePath}</span></div>}
                    {updateStatus.serverUnit && <div>Server unit: <span className="font-mono">{updateStatus.serverUnit}</span></div>}
                    {updateStatus.runnerUnit && <div>Runner unit: <span className="font-mono">{updateStatus.runnerUnit}</span></div>}
                  </div>
                )}
                {recentUpdateLogs.length > 0 && (
                  <div className="rounded-md border px-3 py-2">
                    <div className="mb-2 text-xs font-medium text-foreground/80">Update log</div>
                    <div className="space-y-1">
                      {recentUpdateLogs.map((log) => (
                        <div key={`${log.at}-${log.stage}-${log.message}`} className="grid gap-1 text-xs text-muted-foreground sm:grid-cols-[8rem_1fr]">
                          <span className="font-medium text-foreground/70">{log.stage}</span>
                          <span>{log.message}</span>
                        </div>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            ) : (
              <Button
                onClick={handleUpdate}
                disabled={systemUpdate.isPending || systemInfo?.source.dirty}
                className="inline-flex items-center gap-2 bg-blue-600 hover:bg-blue-700 text-white"
              >
                <RefreshIcon className="h-4 w-4" />
                Update &amp; Restart
              </Button>
            )}
          </div>
        )}
      </div>

      <div className="rounded-md bg-amber-50 border border-amber-200 px-3 py-2">
        <p className="text-xs text-amber-700">
          ⚠ 修改服务器配置请编辑 config.jsonc 并重启
        </p>
      </div>
    </div>
  )
}
