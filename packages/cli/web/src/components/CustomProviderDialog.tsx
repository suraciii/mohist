import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Dialog } from './Dialog'
import { useSaveProvider, useTestProvider } from '../hooks/useQueries'
import type { ProviderFormData } from '../lib/provider-api'

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
      <path fillRule="evenodd" d="M3.28 2.22a.75.75 0 00-1.06 1.06L5.94 7a10.004 10.004 0 019.336 6.41c-1.443-3.75-5.079-6.41-9.336-6.41a10.004 10.004 0 00-2.336.386l2.158 2.158A4.499 4.499 0 008.5 11.5a4.5 4.5 0 00-3.464-4.414L3.28 2.22zM2.39 8.606A8.504 8.504 0 0010 3c4.257 0 7.893 2.66 9.336 6.41a.75.75 0 001.186-.084l2.158-2.158A9.505 9.505 0 0110 1C4.478 1 1.526 5.198.664 9.404l1.726-.798z" clipRule="evenodd" />
      <path d="M13.268 11.5a3.5 3.5 0 01-3.018 3.002A3.5 3.5 0 016.5 11.5a3.5 3.5 0 013.018-3.002 3.5 3.5 0 013.75 3.002z" />
    </svg>
  )
}

interface FormErrors {
  id?: string
  name?: string
  baseURL?: string
  apiKey?: string
  models?: string
}

function validateForm(fields: {
  id: string
  name: string
  baseURL: string
  apiKey: string
  models: string
}): FormErrors {
  const errors: FormErrors = {}

  if (!fields.id.trim()) {
    errors.id = 'Provider ID is required'
  } else if (!/^[a-z0-9-]+$/.test(fields.id.trim())) {
    errors.id = 'Provider ID must contain only lowercase letters, numbers, and hyphens'
  }

  if (!fields.name.trim()) {
    errors.name = 'Name is required'
  }

  if (!fields.baseURL.trim()) {
    errors.baseURL = 'Base URL is required'
  } else {
    try {
      new URL(fields.baseURL.trim())
    } catch {
      errors.baseURL = 'Base URL must be a valid URL (e.g., https://api.example.com)'
    }
  }

  if (!fields.apiKey.trim()) {
    errors.apiKey = 'API Key is required'
  }

  if (!fields.models.trim()) {
    errors.models = 'At least one model is required'
  }

  return errors
}

export function CustomProviderDialog({ open, onClose }: Props) {
  const [id, setId] = useState('')
  const [name, setName] = useState('')
  const [baseURL, setBaseURL] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [models, setModels] = useState('')
  const [showApiKey, setShowApiKey] = useState(false)
  const [testResult, setTestResult] = useState<{ success: boolean; message: string } | null>(null)
  const [lastTestTime, setLastTestTime] = useState(0)
  const [showWarning, setShowWarning] = useState(false)

  const queryClient = useQueryClient()
  const saveProvider = useSaveProvider()
  const testProvider = useTestProvider()

  const handleClose = () => {
    setId('')
    setName('')
    setBaseURL('')
    setApiKey('')
    setModels('')
    setShowApiKey(false)
    setTestResult(null)
    setShowWarning(false)
    onClose()
  }

  const handleSave = () => {
    const fields = {
      id: id.trim(),
      name: name.trim(),
      baseURL: baseURL.trim(),
      apiKey: apiKey.trim(),
      models: models.trim(),
    }

    const errors = validateForm(fields)
    if (Object.keys(errors).length > 0) return

    if (!testResult) {
      setShowWarning(true)
      return
    }

    doSave(fields)
  }

  const doSave = (fields: { id: string; name: string; baseURL: string; apiKey: string; models: string }) => {
    const data: ProviderFormData = {
      name: fields.name,
      apiKey: fields.apiKey,
      baseURL: fields.baseURL,
      models: fields.models.split(',').map(m => m.trim()).filter(Boolean),
      sdk: 'openai-compatible',
    }

    saveProvider.mutate(
      { id: fields.id, data },
      {
        onSuccess: () => {
          queryClient.invalidateQueries({ queryKey: ['providers'] })
          handleClose()
        },
      },
    )
  }

  const handleTest = () => {
    const now = Date.now()
    if (now - lastTestTime < 2000) return
    setLastTestTime(now)

    const fields = {
      id: id.trim(),
      name: name.trim(),
      baseURL: baseURL.trim(),
      apiKey: apiKey.trim(),
      models: models.trim(),
    }

    const errors = validateForm(fields)
    if (Object.keys(errors).length > 0) return

    const data: ProviderFormData = {
      name: fields.name,
      apiKey: fields.apiKey,
      baseURL: fields.baseURL,
      models: fields.models.split(',').map(m => m.trim()).filter(Boolean),
      sdk: 'openai-compatible',
    }

    testProvider.mutate(
      { data },
      {
        onSuccess: (res) => {
          if (res.success) {
            setTestResult({ success: true, message: 'Connection successful!' })
          } else {
            setTestResult({ success: false, message: 'Connection failed. Please check your settings.' })
          }
        },
        onError: (err) => {
          setTestResult({ success: false, message: `Connection failed: ${err.message}` })
        },
      },
    )
  }

  const fields = {
    id: id.trim(),
    name: name.trim(),
    baseURL: baseURL.trim(),
    apiKey: apiKey.trim(),
    models: models.trim(),
  }

  const errors = validateForm(fields)
  const hasErrors = Object.keys(errors).length > 0
  const isFormValid = !hasErrors && fields.id && fields.name && fields.baseURL && fields.apiKey && fields.models

  return (
    <Dialog open={open} onClose={handleClose} title="Add Custom Provider">
      <div className="space-y-4">
        <p className="text-sm text-gray-600">
          Add an OpenAI-compatible provider. All fields are required.
        </p>

        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">
            Provider ID *
          </label>
          <input
            type="text"
            value={id}
            onChange={(e) => {
              setId(e.target.value.toLowerCase())
              setLastTestTime(0)
              setTestResult(null)
            }}
            placeholder="e.g., my-custom-provider"
            className={`w-full rounded-md border px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 ${
              errors.id
                ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
            }`}
            autoFocus
          />
          {errors.id && (
            <p className="mt-1 text-xs text-red-600">{errors.id}</p>
          )}
          <p className="mt-1 text-xs text-gray-500">
            Unique identifier using lowercase letters, numbers, and hyphens
          </p>
        </div>

        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">
            Name *
          </label>
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g., My Custom Provider"
            className={`w-full rounded-md border px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 ${
              errors.name
                ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
            }`}
          />
          {errors.name && (
            <p className="mt-1 text-xs text-red-600">{errors.name}</p>
          )}
          <p className="mt-1 text-xs text-gray-500">
            Display name for this provider
          </p>
        </div>

        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">
            Base URL *
          </label>
          <input
            type="text"
            value={baseURL}
            onChange={(e) => {
              setBaseURL(e.target.value)
              setLastTestTime(0)
              setTestResult(null)
            }}
            placeholder="e.g., https://api.example.com/v1"
            className={`w-full rounded-md border px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 ${
              errors.baseURL
                ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
            }`}
          />
          {errors.baseURL && (
            <p className="mt-1 text-xs text-red-600">{errors.baseURL}</p>
          )}
          <p className="mt-1 text-xs text-gray-500">
            The API endpoint URL for your provider
          </p>
        </div>

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
                setTestResult(null)
              }}
              placeholder="sk-..."
              className={`w-full rounded-md border px-3 py-2 pr-10 text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 ${
                errors.apiKey
                  ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                  : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
              }`}
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
          {errors.apiKey && (
            <p className="mt-1 text-xs text-red-600">{errors.apiKey}</p>
          )}
          <p className="mt-1 text-xs text-gray-500">
            Your API key for authentication
          </p>
        </div>

        <div>
          <label className="block text-xs font-medium text-gray-700 mb-1">
            Models *
          </label>
          <input
            type="text"
            value={models}
            onChange={(e) => setModels(e.target.value)}
            placeholder="e.g., gpt-4, gpt-3.5-turbo, claude-3"
            className={`w-full rounded-md border px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-1 ${
              errors.models
                ? 'border-red-300 focus:border-red-500 focus:ring-red-500'
                : 'border-gray-300 focus:border-blue-500 focus:ring-blue-500'
            }`}
          />
          {errors.models && (
            <p className="mt-1 text-xs text-red-600">{errors.models}</p>
          )}
          <p className="mt-1 text-xs text-gray-500">
            Comma-separated list of available models
          </p>
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

        {showWarning && (
          <div className="rounded-md bg-amber-50 border border-amber-200 px-3 py-3">
            <div className="flex items-start gap-2">
              <svg className="h-5 w-5 text-amber-400 flex-shrink-0 mt-0.5" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clipRule="evenodd" />
              </svg>
              <div className="flex-1">
                <p className="text-sm font-medium text-amber-800">Test Recommended</p>
                <p className="text-xs text-amber-700 mt-1">
                  You haven't tested the connection yet. We recommend testing before saving to ensure your provider is correctly configured.
                </p>
                <div className="flex gap-2 mt-3">
                  <button
                    onClick={() => setShowWarning(false)}
                    className="rounded-md bg-white px-3 py-1.5 text-xs font-medium text-amber-800 border border-amber-300 hover:bg-amber-50 transition-colors"
                  >
                    Test First
                  </button>
                  <button
                    onClick={() => {
                      setShowWarning(false)
                      doSave(fields)
                    }}
                    className="rounded-md bg-amber-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-amber-700 transition-colors"
                  >
                    Save Anyway
                  </button>
                </div>
              </div>
            </div>
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
            disabled={!isFormValid || testProvider.isPending}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 transition-colors"
          >
            {testProvider.isPending ? 'Testing...' : 'Test Connection'}
          </button>
          <button
            onClick={handleSave}
            disabled={!isFormValid || saveProvider.isPending}
            className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            {saveProvider.isPending ? 'Saving...' : 'Save'}
          </button>
        </div>
      </div>
    </Dialog>
  )
}