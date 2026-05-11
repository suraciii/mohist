import { useState, useMemo, useEffect } from 'react'
import { useProviders, useDeleteProvider, useModel, useSetModel, useOpencodeModel, useUpdateOpencodeModel, useStageModels, useSetStageModels, useOpencodeModels } from '../hooks/useQueries'
import { useModels } from '../hooks/useModels'
import type { Provider } from '../lib/provider-api'
import type { Model } from '../lib/types'
import { ProviderConnectDialog } from './ProviderConnectDialog'
import { CustomProviderDialog } from './CustomProviderDialog'
import { ModelSelect } from './ModelSelect'

const DEFAULT_MODEL = 'anthropic/claude-sonnet-4-20250514'
const STAGES = ['explore', 'plan', 'build', 'check', 'integrate'] as const

function PlusIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
    </svg>
  )
}

function TrashIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M8.75 1A2.75 2.75 0 006 3.75v.443c-.795.077-1.584.176-2.365.298a.75.75 0 10.23 1.482l.149-.022.841 10.518A2.75 2.75 0 007.596 19h4.807a2.75 2.75 0 002.742-2.53l.841-10.52.149.023a.75.75 0 00.23-1.482A41.03 41.03 0 0014 4.193V3.75A2.75 2.75 0 0011.25 1h-2.5zM10 4c.84 0 1.673.025 2.5.075V3.75c0-.69-.56-1.25-1.25-1.25h-2.5c-.69 0-1.25.56-1.25 1.25v.325c.827-.05 1.66-.075 2.5-.075z" clipRule="evenodd" />
    </svg>
  )
}

function ChevronRightIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M7.21 8.145a.75.75 0 011.06-.02L10 9.835l1.73-1.71a.75.75 0 011.04 1.08l-2.25 2.22a.75.75 0 01-1.04 0l-2.25-2.22a.75.75 0 01-.02-1.06z" clipRule="evenodd" />
    </svg>
  )
}

function SearchIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z" clipRule="evenodd" />
    </svg>
  )
}

function ProviderIcon({ providerId }: { providerId: string }) {
  const colors: Record<string, string> = {
    openai: 'bg-green-500',
    anthropic: 'bg-orange-500',
    deepseek: 'bg-blue-500',
    google: 'bg-yellow-500',
    azure: 'bg-blue-600',
  }
  const color = colors[providerId.toLowerCase()] ?? 'bg-gray-500'
  return (
    <div className={`w-8 h-8 rounded-md ${color} flex items-center justify-center text-white text-xs font-semibold uppercase`}>
      {providerId.slice(0, 2)}
    </div>
  )
}

const PROVIDER_DESCRIPTIONS: Record<string, string> = {
  anthropic: "Anthropic's Claude models — powerful for complex reasoning and creative tasks.",
  openai: 'OpenAI GPT models — versatile and widely supported.',
  glm: '智谱 GLM models — Chinese language optimized.',
  kimi: 'Moonshot Kimi — long context support up to 128K tokens.',
  minimax: 'MiniMax — competitive pricing with good quality.',
  deepseek: 'DeepSeek — cost-effective for many tasks.',
  qwen: "通义千问 — Alibaba's large language model.",
}

function SourceTag({ source }: { source: 'config' | 'env' | 'none' }) {
  if (source === 'none') return null
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded text-xs font-medium ${
      source === 'config' ? 'bg-blue-100 text-blue-700' : 'bg-green-100 text-green-700'
    }`}>
      {source}
    </span>
  )
}

function ConnectedProviderCard({ provider, onRemove }: { provider: Provider; onRemove: (id: string) => void }) {
  return (
    <div className="flex flex-col md:flex-row md:items-center gap-3 md:gap-4 p-3 border border-gray-200 rounded-lg hover:border-gray-300 transition-colors">
      <div className="flex items-center gap-3 flex-1 min-w-0">
        <span className="text-green-500 text-sm" aria-label="connected">&#9679;</span>
        <ProviderIcon providerId={provider.id} />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <h4 className="text-sm font-medium text-gray-900">{provider.name}</h4>
            {provider.isDefault && (
              <span className="inline-flex items-center px-2 py-0.5 rounded bg-blue-100 text-blue-700 text-xs font-medium">
                default
              </span>
            )}
            <SourceTag source={provider.source} />
          </div>
          <p className="text-xs text-gray-500 mt-0.5 font-mono">{provider.apiKeyMasked}</p>
        </div>
      </div>
      <button
        onClick={() => onRemove(provider.id)}
        className="inline-flex items-center justify-center gap-1.5 px-3 py-1.5 text-sm font-medium text-red-600 hover:text-red-700 hover:bg-red-50 rounded-md transition-colors min-h-[44px] md:min-h-0"
      >
        <TrashIcon className="h-4 w-4" />
        Remove
      </button>
    </div>
  )
}

function AvailableProviderCard({ provider, onConnect }: { provider: Provider; onConnect: (p: Provider) => void }) {
  return (
    <div className="flex flex-col md:flex-row md:items-center gap-3 md:gap-4 p-3 border border-gray-200 rounded-lg hover:border-gray-300 transition-colors">
      <div className="flex items-center gap-3 flex-1 min-w-0">
        <span className="text-gray-300 text-sm" aria-label="not connected">&#9675;</span>
        <ProviderIcon providerId={provider.id} />
        <div className="flex-1 min-w-0">
          <h4 className="text-sm font-medium text-gray-900">{provider.name}</h4>
          <p className="text-xs text-gray-500 mt-0.5">
            {PROVIDER_DESCRIPTIONS[provider.id] || 'Configure this provider to get started.'}
          </p>
        </div>
      </div>
      <button
        onClick={() => onConnect(provider)}
        className="inline-flex items-center justify-center gap-1.5 px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md transition-colors min-h-[44px] md:min-h-0"
      >
        Connect
      </button>
    </div>
  )
}

function CustomProviderCard({ provider, onRemove }: { provider: Provider; onRemove: (id: string) => void }) {
  return (
    <div className="flex flex-col md:flex-row md:items-center gap-3 md:gap-4 p-3 border border-gray-200 rounded-lg hover:border-gray-300 transition-colors">
      <div className="flex items-center gap-3 flex-1 min-w-0">
        <span className="text-green-500 text-sm" aria-label="connected">&#9679;</span>
        <ProviderIcon providerId={provider.id} />
        <div className="flex-1 min-w-0">
          <h4 className="text-sm font-medium text-gray-900">{provider.name}</h4>
          <p className="text-xs text-gray-500 mt-0.5 font-mono">{provider.baseURL}</p>
        </div>
      </div>
      <button
        onClick={() => onRemove(provider.id)}
        className="inline-flex items-center justify-center gap-1.5 px-3 py-1.5 text-sm font-medium text-red-600 hover:text-red-700 hover:bg-red-50 rounded-md transition-colors min-h-[44px] md:min-h-0"
      >
        <TrashIcon className="h-4 w-4" />
        Remove
      </button>
    </div>
  )
}

export function AiSettingsSection() {
  const { data: providers, isLoading, error } = useProviders()
  const deleteProvider = useDeleteProvider()
  const { data: modelProviders } = useModels()
  const { data: modelData } = useModel()
  const setModel = useSetModel()
  const { data: opencodeModelsList } = useOpencodeModels()
  const { data: opencodeModelData } = useOpencodeModel()
  const setOpencodeModel = useUpdateOpencodeModel()
  const { data: stageModelsData } = useStageModels()
  const setStageModels = useSetStageModels()

  const [confirmRemove, setConfirmRemove] = useState<string | null>(null)
  const [connectProvider, setConnectProvider] = useState<Provider | null>(null)
  const [showCustomProvider, setShowCustomProvider] = useState(false)
  const [providerSearch, setProviderSearch] = useState('')
  const [stageOverridesOpen, setStageOverridesOpen] = useState(false)
  const [availableProvidersOpen, setAvailableProvidersOpen] = useState(false)
  const [localStageModels, setLocalStageModels] = useState<Record<string, string>>({})

  useEffect(() => {
    if (stageModelsData?.stageModels) {
      setLocalStageModels(stageModelsData.stageModels)
    }
  }, [stageModelsData])

  const configuredProviders = useMemo(() => (providers?.filter((p) => p.configured && p.isBuiltin) ?? []), [providers])
  const customProviders = useMemo(() => (providers?.filter((p) => p.configured && !p.isBuiltin) ?? []), [providers])
  const unconfiguredProviders = useMemo(() => (providers?.filter((p) => p.isBuiltin && !p.configured) ?? []), [providers])

  const filteredConfigured = useMemo(() => {
    if (!providerSearch.trim()) return configuredProviders
    const q = providerSearch.toLowerCase()
    return configuredProviders.filter(
      (p) => p.name.toLowerCase().includes(q) || p.id.toLowerCase().includes(q),
    )
  }, [configuredProviders, providerSearch])

  const filteredUnconfigured = useMemo(() => {
    if (!providerSearch.trim()) return unconfiguredProviders
    const q = providerSearch.toLowerCase()
    return unconfiguredProviders.filter(
      (p) => p.name.toLowerCase().includes(q) || p.id.toLowerCase().includes(q),
    )
  }, [unconfiguredProviders, providerSearch])

  const availableModels = useMemo(() => {
    if (!modelProviders) return []
    const models: Model[] = []
    for (const provider of modelProviders) {
      if (!provider.configured) continue
      for (const model of provider.models) {
        models.push(model)
      }
    }
    return models.sort((a, b) => a.id.localeCompare(b.id))
  }, [modelProviders])

  const coderModels = useMemo(() => {
    if (!opencodeModelsList) return []
    return opencodeModelsList
      .map((id): Model => ({ id, name: id.split('/').pop() || id, badges: [], contextWindow: 0 }))
      .sort((a, b) => a.id.localeCompare(b.id))
  }, [opencodeModelsList])

  const handleConfirmDisconnect = () => {
    if (confirmRemove) {
      deleteProvider.mutate(confirmRemove, {
        onSuccess: () => setConfirmRemove(null),
      })
    }
  }

  const handleSetModel = (modelId: string) => {
    setModel.mutate(modelId)
  }

  const handleSetOpencodeModel = (modelId: string) => {
    setOpencodeModel.mutate(modelId)
  }

  const handleClearOpencodeModel = () => {
    setOpencodeModel.mutate(null)
  }

  const handleSetStageModel = (stage: string, modelId: string) => {
    const updated = { ...localStageModels, [stage]: modelId }
    setLocalStageModels(updated)
    setStageModels.mutate(updated)
  }

  const handleClearStageModel = (stage: string) => {
    const updated = { ...localStageModels }
    delete updated[stage]
    setLocalStageModels(updated)
    setStageModels.mutate(Object.keys(updated).length > 0 ? updated : null)
  }

  if (isLoading) {
    return (
      <div className="space-y-4">
        <h3 className="text-sm font-medium text-gray-900">AI Providers & Models</h3>
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="h-16 bg-gray-100 rounded-lg animate-pulse" />
          ))}
        </div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="space-y-4">
        <h3 className="text-sm font-medium text-gray-900">AI Providers & Models</h3>
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          Failed to load providers: {(error as Error).message}
        </div>
      </div>
    )
  }

  return (
    <>
      <div className="space-y-8">
        <div className="space-y-4">
          <h3 className="text-sm font-medium text-gray-900">Model Selection</h3>

          <div className="space-y-4">
            <div className="space-y-1.5">
              <label className="block text-xs font-medium text-gray-700">
                Mohist Model
              </label>
              <p className="text-xs text-gray-500">Used for explore stage</p>
              <ModelSelect
                value={modelData?.model ?? DEFAULT_MODEL}
                placeholder={DEFAULT_MODEL}
                models={availableModels}
                onChange={handleSetModel}
              />
            </div>

            <div className="space-y-1.5">
              <label className="block text-xs font-medium text-gray-700">
                Coder Model
              </label>
              <p className="text-xs text-gray-500">Used for explore/plan/build/check/integrate stages</p>
              <ModelSelect
                value={opencodeModelData?.model ?? null}
                placeholder="Same as Mohist Model"
                models={coderModels}
                onChange={handleSetOpencodeModel}
                onClear={handleClearOpencodeModel}
                allowClear
              />
            </div>
          </div>
        </div>

        <hr className="border-gray-100" />

        <div>
          <button
            onClick={() => setStageOverridesOpen(!stageOverridesOpen)}
            className="flex items-center gap-2 w-full text-left"
          >
            <ChevronRightIcon className={`h-4 w-4 text-gray-400 transition-transform ${stageOverridesOpen ? 'rotate-90' : ''}`} />
            <span className="text-sm font-medium text-gray-900">Stage Model Overrides</span>
            <span className="text-xs text-gray-400 ml-1">Advanced</span>
          </button>

          {stageOverridesOpen && (
            <div className="mt-4 space-y-3 pl-6">
              {STAGES.map((stage) => (
                <div key={stage} className="space-y-1">
                  <label className="block text-xs font-medium text-gray-600 capitalize">
                    {stage}
                  </label>
                    <ModelSelect
                      value={localStageModels[stage] ?? null}
                      placeholder="Default"
                      models={coderModels}
                      onChange={(modelId) => handleSetStageModel(stage, modelId)}
                      onClear={() => handleClearStageModel(stage)}
                      allowClear={!!localStageModels[stage]}
                    />
                </div>
              ))}
            </div>
          )}
        </div>

        <hr className="border-gray-100" />

        {(configuredProviders.length > 0 || unconfiguredProviders.length > 0) && (
          <div className="relative">
            <div className="absolute left-3 top-1/2 -translate-y-1/2">
              <SearchIcon className="h-4 w-4 text-gray-400" />
            </div>
            <input
              type="text"
              value={providerSearch}
              onChange={(e) => setProviderSearch(e.target.value)}
              placeholder="Search providers..."
              className="w-full rounded-md border border-gray-300 pl-9 pr-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            />
          </div>
        )}

        {filteredConfigured.length > 0 && (
          <div className="space-y-3">
            <h3 className="text-sm font-medium text-gray-900">Connected Providers</h3>
            <div className="space-y-2">
              {filteredConfigured.map((provider) => (
                <ConnectedProviderCard
                  key={provider.id}
                  provider={provider}
                  onRemove={setConfirmRemove}
                />
              ))}
            </div>
          </div>
        )}

        {filteredUnconfigured.length > 0 && (
          <div>
            <button
              onClick={() => setAvailableProvidersOpen(!availableProvidersOpen)}
              className="flex items-center gap-2 w-full text-left"
            >
              <ChevronRightIcon className={`h-4 w-4 text-gray-400 transition-transform ${availableProvidersOpen ? 'rotate-90' : ''}`} />
              <span className="text-sm font-medium text-gray-900">
                Available ({filteredUnconfigured.length})
              </span>
            </button>

            {availableProvidersOpen && (
              <div className="mt-3 space-y-2">
                {filteredUnconfigured.map((provider) => (
                  <AvailableProviderCard
                    key={provider.id}
                    provider={provider}
                    onConnect={setConnectProvider}
                  />
                ))}
              </div>
            )}
          </div>
        )}

        {providerSearch.trim() && filteredConfigured.length === 0 && filteredUnconfigured.length === 0 && (
          <div className="text-center py-6 border border-dashed border-gray-300 rounded-lg">
            <p className="text-sm text-gray-500">No providers match your search</p>
          </div>
        )}

        <hr className="border-gray-100" />

        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <h3 className="text-sm font-medium text-gray-900">Custom Providers</h3>
            <button
              onClick={() => setShowCustomProvider(true)}
              className="inline-flex items-center gap-1 px-2.5 py-1.5 text-xs font-medium text-gray-600 hover:text-gray-900 hover:bg-gray-100 rounded-md transition-colors"
            >
              <PlusIcon className="h-3.5 w-3.5" />
              Add
            </button>
          </div>

          {customProviders.length === 0 && (
            <p className="text-xs text-gray-500">
              Configure a custom OpenAI-compatible provider
            </p>
          )}

          <div className="space-y-2">
            {customProviders.map((provider) => (
              <CustomProviderCard
                key={provider.id}
                provider={provider}
                onRemove={setConfirmRemove}
              />
            ))}
          </div>
        </div>
      </div>

      {confirmRemove && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
          <div className="bg-white rounded-lg shadow-xl p-6 w-full max-w-sm mx-4">
            <h4 className="text-lg font-medium text-gray-900 mb-2">Remove Provider</h4>
            <p className="text-sm text-gray-600 mb-4">
              Are you sure you want to remove this provider configuration? This action cannot be undone.
            </p>
            <div className="flex justify-end gap-2">
              <button
                onClick={() => setConfirmRemove(null)}
                className="px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-100 rounded-md transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={handleConfirmDisconnect}
                disabled={deleteProvider.isPending}
                className="px-3 py-1.5 text-sm font-medium text-white bg-red-600 hover:bg-red-700 disabled:opacity-50 rounded-md transition-colors"
              >
                {deleteProvider.isPending ? 'Removing...' : 'Remove'}
              </button>
            </div>
          </div>
        </div>
      )}

      <ProviderConnectDialog
        open={connectProvider !== null}
        onClose={() => setConnectProvider(null)}
        provider={connectProvider}
      />

      <CustomProviderDialog
        open={showCustomProvider}
        onClose={() => setShowCustomProvider(false)}
      />
    </>
  )
}
