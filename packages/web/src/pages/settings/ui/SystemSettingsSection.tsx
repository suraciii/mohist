import { useState, useEffect } from 'react'
import { CheckIcon, ClipboardIcon, Loader2Icon } from 'lucide-react'
import {
  isSupersededStatus,
  isTerminalUpdateStatus,
  ProgressStages,
  SystemUpdateOutcomeView,
  useLogLevel,
  useSetLogLevel,
  useSystemInfo,
  useSystemUpdateStatus,
} from '../../../entities/settings'
import type { SystemInfo, SystemUpdateStatusEnvelope } from '../../../entities/settings'
import { Button } from '@/shared/ui/components/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'
import { CardSection } from '@/shared/ui/components/card-section'
import type { SettingsSearchEntry } from '../model/settings-search'
import { getSectionMeta } from '../lib/sections'
import { SectionState } from './SectionState'
import { SettingsSection } from './SettingsSection'
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
      <span className="text-xs text-foreground font-mono text-right tabular-nums">{children}</span>
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

export const SYSTEM_DESCRIPTORS: SettingsSearchEntry[] = [
  {
    tab: 'system',
    label: 'Log Level',
    description: 'Server-side logging verbosity for diagnostics.',
    focusTargetId: 'system-log-level',
  },
  {
    tab: 'system',
    label: 'Source Path',
    description: 'Local source checkout path used by this Mohist runtime.',
    focusTargetId: 'system-source-path',
  },
]

export interface SystemSettingsData {
  logLevelData: { level: string } | undefined
  logLevelLoading: boolean
  logLevelError: boolean
  logLevelErrorValue: Error | null
  setLogLevel: (level: LogLevel) => Promise<unknown>
  systemInfo: SystemInfo | undefined
  infoLoading: boolean
  infoError: boolean
  infoErrorValue: Error | null
  refetchInfo: () => Promise<unknown>
  updateStatusEnvelope: SystemUpdateStatusEnvelope | undefined
  refetchUpdateStatus: () => Promise<unknown>
}

export type SystemSettingsDataHook = () => SystemSettingsData

const useDefaultData: SystemSettingsDataHook = () => {
  const { data: logLevelData, isLoading: logLevelLoading, isError: logLevelError, error: logLevelErrorValue } = useLogLevel()
  const setLogLevelMutation = useSetLogLevel()
  const { data: systemInfo, isLoading: infoLoading, isError: infoError, error: infoErrorValue, refetch: refetchInfo } = useSystemInfo()
  const { data: updateStatusEnvelope, refetch: refetchUpdateStatus } = useSystemUpdateStatus(true)
  return {
    logLevelData,
    logLevelLoading,
    logLevelError,
    logLevelErrorValue,
    setLogLevel: (level) => setLogLevelMutation.mutateAsync(level),
    systemInfo,
    infoLoading,
    infoError,
    infoErrorValue,
    refetchInfo,
    updateStatusEnvelope,
    refetchUpdateStatus,
  }
}

export function SystemSettingsSection({
  dataHook = useDefaultData,
}: {
  dataHook?: SystemSettingsDataHook
} = {}) {
  const {
    logLevelData,
    logLevelLoading,
    logLevelError,
    logLevelErrorValue,
    setLogLevel,
    systemInfo,
    infoLoading,
    infoError,
    infoErrorValue,
    refetchInfo,
    updateStatusEnvelope,
    refetchUpdateStatus,
  } = dataHook()
  const [reconnectState, setReconnectState] = useState<string | null>(null)
  const updateStatus = updateStatusEnvelope?.job ?? null
  const { label: sectionLabel, description: sectionDescription } = getSectionMeta('system')

  const persistedLevel = logLevelData?.level ?? null
  const [currentLevel, setCurrentLevel] = useState<LogLevel | null>(
    persistedLevel && isLogLevel(persistedLevel) ? persistedLevel : null,
  )
  const [saving, setSaving] = useState(false)
  const [logError, setLogError] = useState<string | null>(null)
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')

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
      await setLogLevel(newLevel)
    } catch (err) {
      setCurrentLevel(previousLevel)
      setLogError(err instanceof Error ? err.message : 'Failed to update log level')
    } finally {
      setSaving(false)
    }
  }

  useEffect(() => {
    if (updateStatus && isTerminalUpdateStatus(updateStatus.status)) {
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
  const showProgress = !superseded && (persistedUpdateActive || updateReady || reconnectState === 'Ready')
  const showOutcome = updateStatus
    && (isTerminalUpdateStatus(updateStatus.status) || updateStatus.outcome != null)
  const progressLabel = updateReady ? 'Ready' : reconnectState ?? updateStatus?.stage ?? null
  const updateMessage = updateStatus?.reason ?? systemInfo?.update.reason ?? null
  const recentUpdateLogs = updateStatus?.logs?.slice(-5).reverse() ?? []

  const updateCommand = systemInfo?.source.path
    ? `mo update --repo-root ${systemInfo.source.path}`
    : null

  const handleCopyCommand = async () => {
    if (!updateCommand || !navigator.clipboard?.writeText) {
      setCopyState('failed')
      return
    }
    try {
      await navigator.clipboard.writeText(updateCommand)
      setCopyState('copied')
    } catch {
      setCopyState('failed')
    }
  }

  if (isLoading) {
    return (
      <SectionState
        variant="loading"
        title={sectionLabel}
        description={sectionDescription}
        skeletonRows={6}
      />
    )
  }

  return (
    <SettingsSection title={sectionLabel} description={sectionDescription}>
      <CardSection title="Logging" titleAs="h3">
        {logLevelError ? (
          <p className="text-xs text-muted-foreground">
            {logLevelErrorValue instanceof Error ? logLevelErrorValue.message : 'Log level unavailable'}
          </p>
        ) : (
          <div className="space-y-1.5">
            <label id="system-log-level-label" className="block text-xs font-medium text-muted-foreground">Log Level</label>
            <Select
              value={currentLevel}
              onValueChange={(value) => value && handleLogLevelChange(value)}
              disabled={saving || !currentLevel}
            >
              <SelectTrigger id="system-log-level" aria-labelledby="system-log-level-label" className="w-full max-w-xs">
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
          <div className="mt-3 rounded-md bg-red-50 px-3 py-2 text-xs text-red-700">
            {logError}
          </div>
        )}

        <div className="mt-3 space-y-1">
          <span className="block text-xs font-medium text-muted-foreground">Log Path</span>
          <p
            data-testid="system-log-path"
            className="text-xs text-foreground font-mono tabular-nums"
          >
            {systemInfo?.paths.logs ?? '—'}
          </p>
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
            titleAs="h3"
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

          <CardSection title="Source" titleAs="h3" tone={systemInfo.source.dirty ? 'amber' : 'default'}>
            <InfoRow label="Path">
              <span id="system-source-path" tabIndex={-1} className="rounded-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50">
                {formatValue(systemInfo.source.path)}
              </span>
            </InfoRow>
            <InfoRow label="Branch">{formatValue(systemInfo.source.branch)}</InfoRow>
            <InfoRow label="HEAD">
              <span title={sourceHead ?? undefined}>
                {sourceHead ? `${shortHash(sourceHead)} (${sourceHead})` : 'unknown'}
              </span>
            </InfoRow>
            <InfoRow label="Tree state">{systemInfo.source.dirty ? 'dirty' : 'clean'}</InfoRow>
          </CardSection>

          <CardSection title="Install" titleAs="h3">
            <InfoRow label="Mode">{formatValue(systemInfo.install.mode)}</InfoRow>
            <InfoRow label="Detail">{formatValue(systemInfo.install.reason)}</InfoRow>
            <InfoRow label="Service manager">{formatValue(systemInfo.install.serviceManager)}</InfoRow>
            <InfoRow label="Server unit">{formatValue(systemInfo.install.serverUnit)}</InfoRow>
            <InfoRow label="Runner unit">{formatValue(systemInfo.install.runnerUnit)}</InfoRow>
          </CardSection>

          {systemInfo.install.mode === 'local-source' && (
            <CardSection
              title="Update"
              titleAs="h3"
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
                <p
                  data-testid="system-update-dirty-source-warning"
                  className="mt-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800"
                >
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

              {(updateCommand || showProgress) && (
                <div className="mt-3">
                  <div className="space-y-3">
                    {updateCommand && (
                      <div className="space-y-2">
                        <div className="text-xs text-muted-foreground">Run this command from a terminal to start the update.</div>
                        <div className="flex items-center gap-2">
                          <code data-testid="system-update-command" className="min-w-0 flex-1 break-all rounded-md border bg-muted/30 px-3 py-2 text-xs text-foreground">
                            {updateCommand}
                          </code>
                          <Button type="button" variant="outline" size="icon" onClick={handleCopyCommand} aria-label="Copy update command" title="Copy update command">
                            {copyState === 'copied' ? <CheckIcon className="h-4 w-4" /> : <ClipboardIcon className="h-4 w-4" />}
                          </Button>
                        </div>
                        {copyState !== 'idle' && <p className="text-xs text-muted-foreground">{copyState === 'copied' ? 'Copied' : 'Unable to copy'}</p>}
                      </div>
                    )}
                    {showProgress && <div className="space-y-3">
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
                          <div className="mb-2 text-xs font-medium text-muted-foreground">Update log</div>
                          <div className="space-y-1">
                            {recentUpdateLogs.map((log) => (
                              <div
                                key={`${log.at}-${log.stage}-${log.message}`}
                                className="grid gap-1 text-xs text-muted-foreground sm:grid-cols-[8rem_1fr]"
                              >
                                <span className="font-medium text-muted-foreground">{log.stage}</span>
                                <span>{log.message}</span>
                              </div>
                            ))}
                          </div>
                        </div>
                      )}
                    </div>}
                  </div>
                </div>
              )}
            </CardSection>
          )}

          {systemInfo.install.mode !== 'local-source' && (
            <CardSection title="Update" titleAs="h3">
              <p className="text-xs text-muted-foreground">Web update is unsupported for this deployment.</p>
            </CardSection>
          )}

          <CardSection title="Services" titleAs="h3">
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

          <CardSection title="Paths" titleAs="h3">
            <InfoRow label="Database">{systemInfo.paths.db ?? '—'}</InfoRow>
            <InfoRow label="Config">{systemInfo.paths.config ?? '—'}</InfoRow>
            <InfoRow label="Opencode">{systemInfo.paths.opencode ?? '—'}</InfoRow>
            <InfoRow label="Logs">{systemInfo.paths.logs ?? '—'}</InfoRow>
            <p
              data-testid="system-edit-config-note"
              className="mt-3 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800"
            >
              Modify server-side config by editing config.jsonc and restarting the server.
            </p>
          </CardSection>
        </>
      )}
    </SettingsSection>
  )
}
