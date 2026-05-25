import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Dialog } from './Dialog'
import { useSaveProvider, useTestProvider } from '../hooks/useQueries'
import type { Provider } from '../lib/provider-api'

function CheckCircleIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
    </svg>
  )
}

function XCircleIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z" clipRule="evenodd" />
    </svg>
  )
}

interface Props {
  open: boolean
  onClose: () => void
  provider: Provider | null
}

function EyeIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path d="M10 12.5a2.5 2.5 0 100-5 2.5 2.5 0 000 5z" />
      <path fillRule="evenodd" d="M.664 10.59a1.651 1.651 0 010-1.186A10.004 10.004 0 0110 3c4.257 0 7.893 2.66 9.336 6.41.147.381.233.785.233 1.19v.01a10.004 10.004 0 01-9.336 6.41c-1.443-3.75-5.079-6.41-9.336-6.41A10.004 10.004 0 01.664 9.404c.147.381.233.785.233 1.19v.01a1.651 1.651 0 01-.233 1.186z" clipRule="evenodd" />
    </svg>
  )
}

function EyeSlashIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M3.28 2.22a.75.75 0 00-1.06 1.06L5.94 7a10.004 10.004 0 009.336 6.41c-1.443-3.75-5.079-6.41-9.336-6.41a10.004 10.004 0 00-2.336.386l2.158 2.158A4.499 4.499 0 008.5 11.5a4.5 4.5 0 00-3.464-4.414L3.28 2.22zM2.39 8.606A8.504 8.504 0 0010 3c4.257 0 7.893 2.66 9.336 6.41a.75.75 0 001.186-.084l2.158-2.158A9.505 9.505 0 0110 1C4.478 1 1.526 5.198.664 9.404l1.726-.798z" clipRule="evenodd" />
      <path d="M13.268 11.5a3.5 3.5 0 01-3.018 3.002A3.5 3.5 0 016.5 11.5a3.5 3.5 0 013.018-3.002 3.5 3.5 0 013.75 3.002z" />
    </svg>
  )
}

function providerDescriptions(providerId: string): string {
  const descriptions: Record<string, string> = {
    anthropic: 'Enter your Anthropic API key to enable Claude models.',
    openai: 'Enter your OpenAI API key to enable GPT models.',
    glm: 'Enter your Zhipu GLM API key to enable Chinese language models.',
    kimi: 'Enter your Moonshot Kimi API key for long context support.',
    minimax: 'Enter your MiniMax API key for competitive pricing.',
    deepseek: 'Enter your DeepSeek API key for cost-effective AI.',
    qwen: 'Enter your Qwen API key for Alibaba\'s language model.',
  }
  return descriptions[providerId] || 'Enter your API key to connect.'
}

export function ProviderConnectDialog({ open, onClose, provider }: Props) {
  const [apiKey, setApiKey] = useState('')
  const [showApiKey, setShowApiKey] = useState(false)
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null)
  const [lastTestTime, setLastTestTime] = useState(0)
  const queryClient = useQueryClient()
  const saveProvider = useSaveProvider()
  const testProvider = useTestProvider()

  const handleClose = () => {
    setApiKey('')
    setShowApiKey(false)
    setTestResult(null)
    onClose()
  }

  const handleSave = () => {
    if (!provider || !apiKey.trim()) return
    saveProvider.mutate(
      { id: provider.id, data: { apiKey: apiKey.trim() } },
      {
        onSuccess: () => {
          queryClient.invalidateQueries({ queryKey: ['providers'] })
          queryClient.invalidateQueries({ queryKey: ['models'] })
          handleClose()
        },
      },
    )
  }

  const handleTest = () => {
    if (!provider || !apiKey.trim()) return
    const now = Date.now()
    if (now - lastTestTime < 2000) return
    setLastTestTime(now)
    setTestResult(null)
    testProvider.mutate(
      { data: { apiKey: apiKey.trim(), id: provider.id } },
      {
        onSuccess: (res) => {
          if (res.success) {
            setTestResult({ success: true, message: 'Connection successful! Your API key is valid.' })
          } else {
            setTestResult({ success: false, message: 'Connection failed. Please check your API key and try again.' })
          }
        },
        onError: (err) => {
          setTestResult({ success: false, message: `Connection failed: ${err.message}` })
        },
      },
    )
  }

  if (!provider) return null

  const isValid = apiKey.trim().length > 0

  return (
    <Dialog open={open} onClose={handleClose} title={`Connect ${provider.name}`}>
      <div className="space-y-4">
        <p className="text-sm text-gray-600">
          {providerDescriptions(provider.id)}
        </p>

        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">
            API Key *
          </label>
          <div className="relative">
            <input
              type={showApiKey ? 'text' : 'password'}
              value={apiKey}
              onChange={(e) => {
                setApiKey(e.target.value)
                setLastTestTime(0)
              }}
              placeholder="sk-..."
              className="w-full rounded-md border border-gray-300 px-3 py-2 pr-10 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              autoFocus
            />
            <button
              type="button"
              onClick={() => setShowApiKey(!showApiKey)}
              className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600 p-1"
            >
              {showApiKey ? (
                <EyeSlashIcon className="h-4 w-4" />
              ) : (
                <EyeIcon className="h-4 w-4" />
              )}
            </button>
          </div>
        </div>

        {saveProvider.error && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {(saveProvider.error as Error).message}
          </div>
        )}

        {testResult && (
          <div className={`rounded-md px-3 py-2 text-xs flex items-center gap-2 ${
            testResult.success
              ? 'bg-green-50 text-green-600'
              : 'bg-red-50 text-red-600'
          }`}>
            {testResult.success ? (
              <CheckCircleIcon className="h-4 w-4 flex-shrink-0" />
            ) : (
              <XCircleIcon className="h-4 w-4 flex-shrink-0" />
            )}
            <span>{testResult.message}</span>
          </div>
        )}

        <div className="flex justify-end gap-2 pt-2">
          <button
            onClick={handleClose}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={handleTest}
            disabled={!isValid || testProvider.isPending}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
          >
            {testProvider.isPending ? 'Testing...' : 'Test Connection'}
          </button>
          <button
            onClick={handleSave}
            disabled={!isValid || saveProvider.isPending}
            className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            {saveProvider.isPending ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>
    </Dialog>
  )
}