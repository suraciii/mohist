import { useState, useEffect } from 'react'
import { Loader2Icon, RefreshCwIcon } from 'lucide-react'
import {
  isSupersededStatus,
  isTerminalUpdateStatus,
  ProgressStages,
  SystemUpdateOutcomeView,
  useLogLevel,
  useSetLogLevel,
  useSystemInfo,
  useSystemUpdate,
  useSystemUpdateStatus,
} from '../../../entities/settings'
import { Button } from '@/shared/ui/components/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'
import { CardSection } from '@/shared/ui/components/card-section'
import { SectionState } from './SectionState'
import { ALL_LEVELS, type LogLevel } from '@/shared/lib/log-levels'

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
    <div className="flex items-center justify-between py-2 border-b last:border-b-0 gap-3">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span className="text-xs text-foreground font-mono text-right">{children}</span>
    </div>
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

function isLogLevel(value: string): value is LogLevel {
  return (ALL_LEVELS as readonly string[]).includes(value)
}

export function SystemSettingsSection() {
  const { data: logLevelData, isLoading: logLevelLoading, isError: logLevelError, error: logLevelErrorValue } = useLogLevel()
  const setLogLevelMutation = useSetLogLevel()
  const { data: systemInfo, isLoading: infoLoading, isError: infoError, error: infoErrorValue, refetch: refetchInfo } = useSystemInfo()
  const systemUpdate = useSystemUpdate()
  const [trackingUpdate, setTrackingUpdate] = useState(false)
  const { data: updateStatusEnvelope, refetch: refetchUpdateStatus } = useSystemUpdateStatus(true)
  const [reconnectState, setReconnectState] = useState<string | null>(null)
  const updateStatus = updateStatusEnvelope?.job ?? null

  const persistedLevel = logLevelData?.level ?? null
  const [currentLevel, setCurrentLevel] = useState<LogLevel | null>(
    persistedLevel && isLogLevel(persistedLevel) ? persistedLevel : null,
  )
  const [saving, setSaving] = useState(false)
  const [logError, setLogError] = useState<string | null>(null)

  useEffect(() => {
    if (persistedLevel && isLogLevel(persistedLevel)) {
      setCurrentLevel(persistedLevel)
    }
  }, [persistedLevel])

  const handleLogLevelChange = async (newLevel: string) => {
    if (!isLogLevel(newLevel)) return
    const previousLevel = currentLevel
    setCurrentLevel(newLevel)
    setLogError(null)
    setSaving(true)

    try {
      await setLogLevelMutation.mutateAsync(newLevel)
    } catch (err) {
      setCurrentLevel(previousLevel)
      setLogError(err instanceof Error ? err.message : 'Failed to update log level')
    } finally {
      setSaving(false)
    }
  }

  useEffect(() => {
    if (updateStatus && isTerminalUpdateStatus(updateStatus.status)) {
      setTrackingUpdate(false)
      setReconnectState(null)
      refetchInfo()
      return
    }

    if (updateStatus?.status === 'waiting-for-reconnect') {
      setReconnectState('Waiting for reconnect')
      let cancelled = false
      const fastPollWindowMs = 2 * 60 * 1000
      const slowPollIntervalMs = 30 * 1000
      const startedAt = Date.now()
      let fastPoll = window.setInterval(async () => {
        if (cancelled) return
        const ok = await checkHealth()
        if (cancelled) return
        if (ok) {
          setReconnectState('Refreshing runtime info')
          await refetchInfo()
          await refetchUpdateStatus()
          return
        }
        if (Date.now() - startedAt >= fastPollWindowMs) {
          window.clearInterval(fastPoll)
          fastPoll = window.setInterval(async () => {
            if (cancelled) return
            const ok2 = await checkHealth()
            if (cancelled) return
            if (ok2) {
              setReconnectState('Refreshing runtime info')
              await refetchInfo()
              await refetchUpdateStatus()
            }
          }, slowPollIntervalMs)
        }
      }, 2000)

      return () => {
        cancelled = true
        clearInterval(fastPoll)
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
  const superseded = isSupersededStatus(updateStatus?.status)
  const updateReady = updateStatus?.status === 'succeeded'
    || (updateStatus?.status === 'waiting-for-reconnect' && !!gitHash && gitHash === updateStatus.sourceHead)
  const persistedUpdateActive = updateStatus?.status === 'running' || updateStatus?.status === 'waiting-for-reconnect'
  const showUpdateButton = systemInfo?.install.mode === 'local-source'
    && systemInfo.update.available
    && systemInfo.update.status === 'update-available'
    && !persistedUpdateActive
    && !trackingUpdate
  const showProgress = !superseded && (trackingUpdate || persistedUpdateActive || updateReady || reconnectState === 'Ready')
  const showOutcome = updateStatus
    && (isTerminalUpdateStatus(updateStatus.status) || updateStatus.outcome != null)
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
      <SectionState
        variant="loading"
        title="System"
        description="Logging, runtime identity, and local-source update status."
        skeletonRows={6}
      />
    )
  }

  return (
    <div className="space-y-6">
      <CardSection title="Logging">
        {logLevelError ? (
          <p className="text-xs text-muted-foreground">
            {logLevelErrorValue instanceof Error ? logLevelErrorValue.message : 'Log level unavailable'}
          </p>
        ) : (
          <div className="space-y-1.5">
            <label className="block text-xs font-medium text-foreground/80">Log Level</label>
            <Select
              value={currentLevel ?? undefined}
              onValueChange={(value) => value && handleLogLevelChange(value)}
              disabled={saving || !currentLevel}
            >
              <SelectTrigger className="w-full max-w-xs">
                <SelectValue placeholder="Select log level" />
              </SelectTrigger>
              <SelectContent>
                {ALL_LEVELS.map((level) => (
                  <SelectItem key={level} value={level}>
                    {level}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {saving && <p className="text-xs text-muted-foreground/70">Saving...</p>}
          </div>
        )}

        {logError && (
          <div className="mt-3 rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {logError}
          </div>
        )}

        <div className="mt-3 space-y-1">
          <span className="block text-xs font-medium text-foreground/80">Log Path</span>
          <p className="text-xs text-muted-foreground font-mono">~/.mohist/logs/</p>
        </div>
      </CardSection>

      {infoError || !systemInfo ? (
        <SectionState
          variant="error"
          title="Server Runtime"
          message={infoErrorValue instanceof Error ? infoErrorValue.message : 'runtime info unavailable'}
          onRetry={() => refetchInfo()}
        />
      ) : (
        <>
          <CardSection
            title="Identity"
            tone={superseded ? 'blue' : 'default'}
          >
            <InfoRow label="Running version">{formatValue(systemInfo.running.version)}</InfoRow>
            <InfoRow label="Running git hash">
              <span title={gitHash ?? undefined}>{shortHash(gitHash)}</span>
            </InfoRow>
            <InfoRow label="Started at">{formatTimestamp(systemInfo.running.startedAt)}</InfoRow>
            {superseded && systemInfo.running.version && (
              <p
                data-testid="system-update-superseded-runtime"
                className="mt-2 text-xs text-muted-foreground"
              >
                Current runtime: v{systemInfo.running.version}
                {gitHash ? ` (${shortHash(gitHash)})` : ''}
              </p>
            )}
          </CardSection>

          <CardSection title="Source" tone={systemInfo.source.dirty ? 'amber' : 'default'}>
            <InfoRow label="Path">{formatValue(systemInfo.source.path)}</InfoRow>
            <InfoRow label="Branch">{formatValue(systemInfo.source.branch)}</InfoRow>
            <InfoRow label="HEAD">
              <span title={sourceHead ?? undefined}>
                {sourceHead ? `${shortHash(sourceHead)} (${sourceHead})` : 'unknown'}
              </span>
            </InfoRow>
            <InfoRow label="Tree state">{systemInfo.source.dirty ? 'dirty' : 'clean'}</InfoRow>
          </CardSection>

          <CardSection title="Install">
            <InfoRow label="Mode">{formatValue(systemInfo.install.mode)}</InfoRow>
            <InfoRow label="Detail">{formatValue(systemInfo.install.reason)}</InfoRow>
            <InfoRow label="Service manager">{formatValue(systemInfo.install.serviceManager)}</InfoRow>
            <InfoRow label="Server unit">{formatValue(systemInfo.install.serverUnit)}</InfoRow>
            <InfoRow label="Runner unit">{formatValue(systemInfo.install.runnerUnit)}</InfoRow>
          </CardSection>

          {systemInfo.install.mode === 'local-source' && (
            <CardSection
              title="Update"
              tone={
                superseded
                  ? 'blue'
                  : systemInfo.update.available
                    ? (updateReady ? 'green' : 'amber')
                    : 'default'
              }
            >
              <InfoRow label="Status">{formatValue(systemInfo.update.status)}</InfoRow>

              {updateMessage && (
                <p className="mt-2 text-xs text-muted-foreground">{updateMessage}</p>
              )}

              {systemInfo.source.dirty && (
                <p className="mt-2 rounded-md bg-amber-100 border border-amber-200 px-3 py-2 text-xs text-amber-800">
                  Local source has uncommitted changes. Update is disabled until the tree is clean.
                </p>
              )}

              {showOutcome && updateStatus && (
                <div
                  data-testid="system-update-outcome-block"
                  className="mt-3"
                >
                  <SystemUpdateOutcomeView job={updateStatus} />
                </div>
              )}

              {(showUpdateButton || showProgress) && (
                <div className="mt-3">
                  {showProgress ? (
                    <div className="space-y-3">
                      <span className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-amber-600 bg-amber-50 rounded-md">
                        {!updateReady && <Loader2Icon className="h-4 w-4 animate-spin" />}
                        {progressLabel ?? 'Waiting for reconnect'}
                      </span>
                      <ProgressStages job={updateStatus} />
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
                              <div
                                key={`${log.at}-${log.stage}-${log.message}`}
                                className="grid gap-1 text-xs text-muted-foreground sm:grid-cols-[8rem_1fr]"
                              >
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
                      disabled={systemUpdate.isPending || systemInfo.source.dirty}
                      className="inline-flex items-center gap-2 bg-blue-600 hover:bg-blue-700 text-white"
                    >
                      <RefreshCwIcon className="h-4 w-4" />
                      Update &amp; Restart
                    </Button>
                  )}
                </div>
              )}
            </CardSection>
          )}

          {systemInfo.install.mode !== 'local-source' && (
            <CardSection title="Update">
              <p className="text-xs text-muted-foreground">Web update is unsupported for this deployment.</p>
            </CardSection>
          )}

          <CardSection title="Services">
            <InfoRow label="Server">
              <span className="inline-flex items-center gap-2">
                {formatValue(systemInfo.services.server)} <StatusBadge status={systemInfo.services.server} />
              </span>
            </InfoRow>
            <InfoRow label="Runner">
              <span className="inline-flex items-center gap-2">
                {formatValue(systemInfo.services.runner)} <StatusBadge status={systemInfo.services.runner} />
              </span>
            </InfoRow>
          </CardSection>

          <CardSection title="Paths">
            <InfoRow label="Database">{systemInfo.paths.db ?? '—'}</InfoRow>
            <InfoRow label="Config">{systemInfo.paths.config ?? '—'}</InfoRow>
            <InfoRow label="Opencode">{systemInfo.paths.opencode ?? '—'}</InfoRow>
            <InfoRow label="Logs">{systemInfo.paths.logs ?? '—'}</InfoRow>
          </CardSection>
        </>
      )}

      <div className="rounded-md bg-amber-50 border border-amber-200 px-3 py-2">
        <p className="text-xs text-amber-700">
          Modify server-side config by editing config.jsonc and restarting the server.
        </p>
      </div>
    </div>
  )
}
