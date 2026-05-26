import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Dialog } from './Dialog'
import { useSaveProvider } from '../hooks/useQueries'
import type { ProviderFormData } from '../lib/provider-api'

interface Props {
  open: boolean
  onClose: () => void
}

interface FormErrors {
  id?: string
  name?: string
  baseURL?: string
  models?: string
}

function validateForm(fields: {
  id: string
  name: string
  baseURL: string
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

  if (!fields.models.trim()) {
    errors.models = 'At least one model is required'
  }

  return errors
}

export function CustomProviderDialog({ open, onClose }: Props) {
  const [id, setId] = useState('')
  const [name, setName] = useState('')
  const [baseURL, setBaseURL] = useState('')
  const [models, setModels] = useState('')

  const queryClient = useQueryClient()
  const saveProvider = useSaveProvider()

  const handleClose = () => {
    setId('')
    setName('')
    setBaseURL('')
    setModels('')
    onClose()
  }

  const handleSave = () => {
    const fields = {
      id: id.trim(),
      name: name.trim(),
      baseURL: baseURL.trim(),
      models: models.trim(),
    }

    const errors = validateForm(fields)
    if (Object.keys(errors).length > 0) return

    doSave(fields)
  }

  const doSave = (fields: { id: string; name: string; baseURL: string; models: string }) => {
    const data: ProviderFormData = {
      name: fields.name,
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

  const fields = {
    id: id.trim(),
    name: name.trim(),
    baseURL: baseURL.trim(),
    models: models.trim(),
  }

  const errors = validateForm(fields)
  const hasErrors = Object.keys(errors).length > 0
  const isFormValid = !hasErrors && fields.id && fields.name && fields.baseURL && fields.models

  return (
    <Dialog open={open} onClose={handleClose} title="Add Custom Provider Catalog">
      <div className="space-y-4">
        <p className="text-sm text-gray-600">
          Add a provider and model list for selection. Mohist does not authenticate with the provider.
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
            onChange={(e) => setBaseURL(e.target.value)}
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

        <div className="rounded-md border border-blue-100 bg-blue-50 px-3 py-2 text-xs text-blue-700">
          Configure credentials in the external coder agent. Mohist only stores this catalog entry so workflows can pass model ids.
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <button
            onClick={handleClose}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
          >
            Cancel
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
