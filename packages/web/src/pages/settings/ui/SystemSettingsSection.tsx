import { useState, useEffect, useCallback, useRef } from 'react'
import { useLogLevel, useSetLogLevel, useSystemInfo, useRebuildSystem } from '../../../entities/settings/api/queries'
import { Button } from '@/components/ui/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

const LOG_LEVELS = ['DEBUG', 'INFO', 'WARN', 'ERROR'] as const
const DEFAULT_LOG_LEVEL = 'INFO'

function StatusBadge({ running }: { running: boolean }) {
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

function CheckCircleIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
    </svg>
  )
}

function ExclamationIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9.75a.75.75 0 01.75.75v.5a.75.75 0 01-1.5 0v-.5a.75.75 0 01.75-.75z" clipRule="evenodd" />
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

export function SystemSettingsSection() {
  const { data: logLevelData, isLoading: logLevelLoading } = useLogLevel()
  const setLogLevel = useSetLogLevel()
  const { data: systemInfo, isLoading: infoLoading, isError: infoError, refetch: refetchInfo } = useSystemInfo()
  const rebuildSystem = useRebuildSystem()

  const [currentLevel, setCurrentLevel] = useState(DEFAULT_LOG_LEVEL)
  const [saving, setSaving] = useState(false)
  const [logError, setLogError] = useState<string | null>(null)

  const [reconnectState, setReconnectState] = useState<'idle' | 'rebuilding' | 'restarting' | 'reconnecting'>('idle')
  const [countdown, setCountdown] = useState(60)
  const countdownRef = useRef<number | null>(null)
  const healthCheckRef = useRef<number | null>(null)

  const clearTimers = useCallback(() => {
    if (countdownRef.current !== null) {
      clearInterval(countdownRef.current)
      countdownRef.current = null
    }
    if (healthCheckRef.current !== null) {
      clearInterval(healthCheckRef.current)
      healthCheckRef.current = null
    }
  }, [])

  useEffect(() => {
    return () => clearTimers()
  }, [clearTimers])

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

  const handleRebuild = useCallback(async () => {
    if (reconnectState !== 'idle') return
    setReconnectState('rebuilding')

    try {
      await rebuildSystem.mutateAsync()
    } catch {
      setReconnectState('idle')
      return
    }

    setReconnectState('restarting')
    setCountdown(60)

    countdownRef.current = window.setInterval(() => {
      setCountdown((c) => {
        if (c <= 1) {
          if (countdownRef.current !== null) {
            clearInterval(countdownRef.current)
            countdownRef.current = null
          }
          return 0
        }
        return c - 1
      })
    }, 1000)

    healthCheckRef.current = window.setInterval(async () => {
      const ok = await checkHealth()
      if (ok) {
        clearTimers()
        setReconnectState('idle')
        refetchInfo()
      }
    }, 5000)
  }, [reconnectState, rebuildSystem, clearTimers, refetchInfo])

  const serverRunning = !infoError && systemInfo?.server?.status === 'running'
  const isLoading = logLevelLoading || infoLoading

  const sourceHead = systemInfo?.sourceHead ?? null
  const gitHash = systemInfo?.gitHash ?? null
  const upToDate = sourceHead === null || sourceHead === gitHash
  const showRebuildButton = sourceHead !== null && !upToDate && reconnectState === 'idle'

  const rebuildButton = () => {
    if (reconnectState === 'rebuilding') {
      return (
        <Button
          disabled
          className="inline-flex items-center gap-2 bg-blue-400 cursor-not-allowed"
        >
          <SpinnerIcon className="h-4 w-4" />
          Rebuilding...
        </Button>
      )
    }
    if (reconnectState === 'restarting') {
      return (
        <span className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-amber-600 bg-amber-50 rounded-md">
          <SpinnerIcon className="h-4 w-4" />
          Restarting... reconnecting in {countdown}s
        </span>
      )
    }
    if (reconnectState === 'reconnecting') {
      return (
        <span className="inline-flex items-center gap-2 px-4 py-2 text-sm font-medium text-muted-foreground bg-muted rounded-md">
          <SpinnerIcon className="h-4 w-4" />
          Reconnecting...
        </span>
      )
    }
    if (showRebuildButton) {
      return (
        <Button
          onClick={handleRebuild}
          className="inline-flex items-center gap-2 bg-blue-600 hover:bg-blue-700 text-white"
        >
          <RefreshIcon className="h-4 w-4" />
          Rebuild &amp; Restart
        </Button>
      )
    }
    return null
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
        <p className="text-xs text-muted-foreground mt-1">Logging and runtime information.</p>
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
        <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">About</h4>

        <div className="rounded-md border px-4 py-1">
          <InfoRow label="Mohist">
            v{systemInfo?.version ?? 'unknown'} · Git {gitHash ?? 'unknown'}
          </InfoRow>
          {sourceHead && (
            <InfoRow label="Source HEAD">
              {sourceHead}
            </InfoRow>
          )}
          <InfoRow label="Status">
            {upToDate ? (
              <span className="inline-flex items-center gap-1.5 text-green-600">
                <CheckCircleIcon className="h-3.5 w-3.5" />
                Up to date
              </span>
            ) : (
              <span className="inline-flex items-center gap-1.5 text-amber-600">
                <ExclamationIcon className="h-3.5 w-3.5" />
                Source changed — rebuild needed
              </span>
            )}
          </InfoRow>
          <InfoRow label="Server">
            {systemInfo ? (
              <>
                {systemInfo.server.host}:{systemInfo.server.port}{' '}
                <StatusBadge running={serverRunning} />
              </>
            ) : (
              <>
                — <StatusBadge running={false} />
              </>
            )}
          </InfoRow>
          <InfoRow label="Database">
            {systemInfo?.paths?.db ?? '—'}
          </InfoRow>
          <InfoRow label="Config">
            {systemInfo?.paths?.config ?? '—'}
          </InfoRow>
          <InfoRow label="Opencode">
            {systemInfo?.paths?.opencode ?? '—'}
          </InfoRow>
        </div>

        {rebuildButton() && (
          <div className="pt-2">
            {rebuildButton()}
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
