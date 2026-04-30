import { useState, useEffect } from 'react'
import { useLogLevel, useSetLogLevel, useSystemInfo } from '../hooks/useQueries'

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
    <span className="inline-flex items-center gap-1 text-xs font-medium text-gray-500">
      <span className="w-1.5 h-1.5 rounded-full bg-gray-400" />
      Stopped
    </span>
  )
}

function InfoRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between py-2 border-b border-gray-100 last:border-b-0">
      <span className="text-xs text-gray-500">{label}</span>
      <span className="text-xs text-gray-900 font-mono">{children}</span>
    </div>
  )
}

export function SystemSettingsSection() {
  const { data: logLevelData, isLoading: logLevelLoading } = useLogLevel()
  const setLogLevel = useSetLogLevel()
  const { data: systemInfo, isLoading: infoLoading, isError: infoError } = useSystemInfo()

  const [currentLevel, setCurrentLevel] = useState(DEFAULT_LOG_LEVEL)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (logLevelData?.level) {
      setCurrentLevel(logLevelData.level)
    }
  }, [logLevelData])

  const handleLogLevelChange = async (newLevel: string) => {
    const previousLevel = currentLevel
    setCurrentLevel(newLevel)
    setError(null)
    setSaving(true)

    try {
      await setLogLevel.mutateAsync(newLevel)
    } catch (err) {
      setCurrentLevel(previousLevel)
      setError(err instanceof Error ? err.message : 'Failed to update log level')
    } finally {
      setSaving(false)
    }
  }

  const serverRunning = !infoError && systemInfo?.server?.status === 'running'

  const isLoading = logLevelLoading || infoLoading

  if (isLoading) {
    return (
      <div className="space-y-6">
        <h3 className="text-sm font-medium text-gray-900">System</h3>
        <div className="space-y-1.5">
          <div className="h-4 w-24 bg-gray-100 rounded animate-pulse" />
          <div className="h-9 w-full bg-gray-100 rounded-md animate-pulse" />
        </div>
        <div className="space-y-2">
          {[1, 2, 3, 4, 5].map((i) => (
            <div key={i} className="h-8 bg-gray-100 rounded animate-pulse" />
          ))}
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-8">
      <div>
        <h3 className="text-sm font-medium text-gray-900">System</h3>
        <p className="text-xs text-gray-500 mt-1">Logging and runtime information.</p>
      </div>

      <div className="space-y-3">
        <h4 className="text-xs font-semibold text-gray-500 uppercase tracking-wider">Logging</h4>

        <div className="space-y-1.5">
          <label className="block text-xs font-medium text-gray-700">Log Level</label>
          <select
            value={currentLevel}
            onChange={(e) => handleLogLevelChange(e.target.value)}
            disabled={saving}
            className="w-full max-w-xs px-3 py-1.5 border border-gray-300 rounded-md text-sm bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 disabled:opacity-50"
          >
            {LOG_LEVELS.map((level) => (
              <option key={level} value={level}>
                {level}
              </option>
            ))}
          </select>
          {saving && <p className="text-xs text-gray-400">Saving...</p>}
        </div>

        {error && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {error}
          </div>
        )}

        <div className="space-y-1">
          <span className="block text-xs font-medium text-gray-700">Log Path</span>
          <p className="text-xs text-gray-500 font-mono">~/.mohist/logs/</p>
        </div>
      </div>

      <hr className="border-gray-100" />

      <div className="space-y-3">
        <h4 className="text-xs font-semibold text-gray-500 uppercase tracking-wider">About</h4>

        <div className="rounded-md border border-gray-200 px-4 py-1">
          <InfoRow label="Mohist">
            v{systemInfo?.version ?? 'unknown'} · Git {systemInfo?.gitHash ?? 'unknown'}
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
      </div>

      <div className="rounded-md bg-amber-50 border border-amber-200 px-3 py-2">
        <p className="text-xs text-amber-700">
          ⚠ 修改服务器配置请编辑 config.jsonc 并重启
        </p>
      </div>
    </div>
  )
}
