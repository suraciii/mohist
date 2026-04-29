import { useState, useEffect, useRef, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import { useProviders, useDeleteProvider } from '../hooks/useQueries'
import { useProviderGroups } from '../hooks/useProviderGroups'
import type { Provider } from '../lib/provider-api'
import { ProviderConnectDialog } from './ProviderConnectDialog'
import { CustomProviderDialog } from './CustomProviderDialog'
import { ProviderGroup } from './ProviderGroup'

interface TabProps {
  active?: boolean
  onClick?: () => void
  children: React.ReactNode
}

function Tab({ active, onClick, children }: TabProps) {
  return (
    <button
      onClick={onClick}
      className={`px-4 py-2 text-sm font-medium border-b-2 transition-colors ${
        active
          ? 'border-blue-600 text-blue-600'
          : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
      }`}
    >
      {children}
    </button>
  )
}

function TabPanel({ children }: { children: React.ReactNode }) {
  return <div className="py-4">{children}</div>
}

function CloudIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
    </svg>
  )
}

function CloudOffIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M9.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5zm-3.03 7.28a.75.75 0 00-1.06-1.06L2.47 14.97a.75.75 0 000 1.06l3.19 3.19a.75.75 0 001.06-1.06L4.56 15.56l2.19-2.28z" clipRule="evenodd" />
    </svg>
  )
}

function CheckIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M16.704 4.153a.75.75 0 01.143 1.052l-8 10.5a.75.75 0 01-1.127.075l-4.5-4.5a.75.75 0 011.06-1.06l3.894 3.893 7.48-9.817z" clipRule="evenodd" />
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

function SourceTag({ source }: { source: 'config' | 'env' | 'none' }) {
  if (source === 'none') return null
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded text-xs font-medium ${
      source === 'config' ? 'bg-blue-100 text-blue-700' : 'bg-green-100 text-green-700'
    }`}>
      {source === 'config' ? <CloudIcon className="h-3 w-3" /> : <CloudOffIcon className="h-3 w-3" />}
      {source}
    </span>
  )
}

const providerDescriptions: Record<string, string> = {
  anthropic: "Anthropic's Claude models - powerful for complex reasoning and creative tasks.",
  openai: 'OpenAI GPT models - versatile and widely supported.',
  glm: '智谱 GLM models - Chinese language optimized.',
  kimi: 'Moonshot Kimi - long context support up to 128K tokens.',
  minimax: 'MiniMax - competitive pricing with good quality.',
  deepseek: 'DeepSeek - cost-effective for many tasks.',
  qwen: "通义千问 - Alibaba's large language model.",
}

interface ConnectedProviderCardProps {
  provider: Provider
  onDisconnect: (id: string) => void
}

function ConnectedProviderCard({ provider, onDisconnect }: ConnectedProviderCardProps) {
  return (
    <div className="flex flex-col md:flex-row md:items-center gap-3 md:gap-4 p-4 border border-gray-200 rounded-lg hover:border-gray-300 transition-colors">
      <div className="flex items-center gap-3 md:gap-4 flex-1 min-w-0">
        <ProviderIcon providerId={provider.id} />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <h4 className="text-sm font-medium text-gray-900">{provider.name}</h4>
            {provider.isDefault && (
              <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-blue-100 text-blue-700 text-xs font-medium">
                <CheckIcon className="h-3 w-3" />
                default
              </span>
            )}
            <SourceTag source={provider.source} />
          </div>
          <p className="text-xs text-gray-500 mt-0.5 font-mono">{provider.apiKeyMasked}</p>
        </div>
      </div>
      <button
        onClick={() => onDisconnect(provider.id)}
        className="inline-flex items-center justify-center gap-1.5 px-3 py-1.5 text-sm font-medium text-red-600 hover:text-red-700 hover:bg-red-50 rounded-md transition-colors min-h-[44px]"
      >
        <TrashIcon className="h-4 w-4" />
        Remove
      </button>
    </div>
  )
}

interface AvailableProviderCardProps {
  provider: Provider
  onConnect: (provider: Provider) => void
}

function AvailableProviderCard({ provider, onConnect }: AvailableProviderCardProps) {
  return (
    <div className="flex flex-col md:flex-row md:items-center gap-3 md:gap-4 p-4 border border-gray-200 rounded-lg hover:border-gray-300 transition-colors">
      <div className="flex items-center gap-3 md:gap-4 flex-1 min-w-0">
        <ProviderIcon providerId={provider.id} />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <h4 className="text-sm font-medium text-gray-900">{provider.name}</h4>
          </div>
          <p className="text-xs text-gray-500 mt-0.5">
            {providerDescriptions[provider.id] || 'Configure this provider to get started.'}
          </p>
        </div>
      </div>
      <button
        onClick={() => onConnect(provider)}
        className="inline-flex items-center justify-center gap-1.5 px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md transition-colors min-h-[44px]"
      >
        <CloudIcon className="h-4 w-4" />
        Connect
      </button>
    </div>
  )
}

function useDebouncedValue<T>(value: T, delay: number): T {
  const [debounced, setDebounced] = useState(value)

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delay)
    return () => clearTimeout(timer)
  }, [value, delay])

  return debounced
}

function ConfirmRemoveDialog({
  isPending,
  onConfirm,
  onCancel,
}: {
  isPending: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
      <div className="bg-white rounded-lg shadow-xl p-6 w-full max-w-sm mx-4">
        <h4 className="text-lg font-medium text-gray-900 mb-2">Remove Provider</h4>
        <p className="text-sm text-gray-600 mb-4">
          Are you sure you want to remove this provider configuration? This action cannot be undone.
        </p>
        <div className="flex justify-end gap-2">
          <button
            onClick={onCancel}
            className="px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-100 rounded-md transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            disabled={isPending}
            className="px-3 py-1.5 text-sm font-medium text-white bg-red-600 hover:bg-red-700 disabled:opacity-50 rounded-md transition-colors"
          >
            {isPending ? 'Removing...' : 'Remove'}
          </button>
        </div>
      </div>
    </div>
  )
}

export function SettingsPage() {
  const [activeTab, setActiveTab] = useState('providers')
  const navigate = useNavigate()

  const { data: providers, isLoading, error } = useProviders()
  const deleteProvider = useDeleteProvider()

  const [searchInput, setSearchInput] = useState('')
  const debouncedSearch = useDebouncedValue(searchInput, 300)
  const searchRef = useRef<HTMLInputElement>(null)

  const [confirmRemove, setConfirmRemove] = useState<string | null>(null)
  const [connectProvider, setConnectProvider] = useState<Provider | null>(null)
  const [showCustomProvider, setShowCustomProvider] = useState(false)

  const allProviders = providers ?? []
  const { groups, isSearching } = useProviderGroups(allProviders, debouncedSearch)

  const handleConfirmDisconnect = useCallback(() => {
    if (confirmRemove) {
      deleteProvider.mutate(confirmRemove, {
        onSuccess: () => setConfirmRemove(null),
      })
    }
  }, [confirmRemove, deleteProvider])

  const handleSearchKeyDown = useCallback((e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Escape') {
      setSearchInput('')
      searchRef.current?.blur()
    }
  }, [])

  if (activeTab !== 'providers') {
    return (
      <div className="flex-1 bg-gray-50">
        <div className="max-w-4xl mx-auto px-4 md:px-6 py-6">
          <div className="mb-6">
            <h1 className="text-xl font-semibold text-gray-900">Settings</h1>
          </div>

          <div className="md:hidden mb-4">
            <button
              onClick={() => navigate('/logs')}
              className="inline-flex items-center gap-2 w-full px-4 py-3 text-sm font-medium text-gray-700 bg-white border border-gray-200 rounded-lg hover:bg-gray-50 transition-colors min-h-[44px]"
            >
              <svg className="h-4 w-4 text-gray-500" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M6 2a2 2 0 00-2 2v12a2 2 0 002 2h8a2 2 0 002-2V4a2 2 0 00-2-2H6zm1 2a1 1 0 000 2h6a1 1 0 100-2H7zm6 7a1 1 0 011 1v3a1 1 0 11-2 0v-3a1 1 0 011-1zm-3 3a1 1 0 011 1v1a1 1 0 11-2 0v-1a1 1 0 011-1zm-2-2a1 1 0 10-2 0v3a1 1 0 102 0v-3z" clipRule="evenodd" />
              </svg>
              View Logs
              <svg className="h-4 w-4 ml-auto text-gray-400" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
              </svg>
            </button>
          </div>

          <div className="bg-white rounded-lg border border-gray-200 shadow-sm">
            <div className="border-b border-gray-200">
              <nav className="flex gap-1 px-4">
                <Tab active={activeTab === 'providers'} onClick={() => setActiveTab('providers')}>
                  Providers
                </Tab>
                <Tab active={activeTab === 'general'} onClick={() => setActiveTab('general')}>
                  General
                </Tab>
              </nav>
            </div>
            <div className="p-6">
              <TabPanel>
                <p className="text-sm text-gray-500">General settings coming soon.</p>
              </TabPanel>
            </div>
          </div>
        </div>
      </div>
    )
  }

  if (isLoading) {
    return (
      <div className="flex-1 bg-gray-50">
        <div className="max-w-4xl mx-auto px-4 md:px-6 py-6">
          <div className="mb-6">
            <h1 className="text-xl font-semibold text-gray-900">Settings</h1>
          </div>
          <div className="bg-white rounded-lg border border-gray-200 shadow-sm">
            <div className="border-b border-gray-200">
              <nav className="flex gap-1 px-4">
                <Tab active onClick={() => setActiveTab('providers')}>Providers</Tab>
                <Tab active={false} onClick={() => setActiveTab('general')}>General</Tab>
              </nav>
            </div>
            <div className="p-6">
              <div className="space-y-3">
                {[1, 2, 3].map(i => (
                  <div key={i} className="h-20 bg-gray-100 rounded-lg animate-pulse" />
                ))}
              </div>
            </div>
          </div>
        </div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="flex-1 bg-gray-50">
        <div className="max-w-4xl mx-auto px-4 md:px-6 py-6">
          <div className="mb-6">
            <h1 className="text-xl font-semibold text-gray-900">Settings</h1>
          </div>
          <div className="bg-white rounded-lg border border-gray-200 shadow-sm">
            <div className="border-b border-gray-200">
              <nav className="flex gap-1 px-4">
                <Tab active onClick={() => setActiveTab('providers')}>Providers</Tab>
                <Tab active={false} onClick={() => setActiveTab('general')}>General</Tab>
              </nav>
            </div>
            <div className="p-6">
              <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                Failed to load providers: {(error as Error).message}
              </div>
            </div>
          </div>
        </div>
      </div>
    )
  }

  const renderProviderCard = (provider: Provider) => {
    if (provider.configured) {
      return (
        <ConnectedProviderCard
          key={provider.id}
          provider={provider}
          onDisconnect={(id) => setConfirmRemove(id)}
        />
      )
    }
    if (provider.isBuiltin) {
      return (
        <AvailableProviderCard
          key={provider.id}
          provider={provider}
          onConnect={(p) => setConnectProvider(p)}
        />
      )
    }
    return null
  }

  return (
    <div className="flex-1 bg-gray-50">
      <div className="max-w-4xl mx-auto px-4 md:px-6 py-6">
        <div className="mb-6">
          <h1 className="text-xl font-semibold text-gray-900">Settings</h1>
        </div>

        <div className="md:hidden mb-4">
          <button
            onClick={() => navigate('/logs')}
            className="inline-flex items-center gap-2 w-full px-4 py-3 text-sm font-medium text-gray-700 bg-white border border-gray-200 rounded-lg hover:bg-gray-50 transition-colors min-h-[44px]"
          >
            <svg className="h-4 w-4 text-gray-500" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M6 2a2 2 0 00-2 2v12a2 2 0 002 2h8a2 2 0 002-2V4a2 2 0 00-2-2H6zm1 2a1 1 0 000 2h6a1 1 0 100-2H7zm6 7a1 1 0 011 1v3a1 1 0 11-2 0v-3a1 1 0 011-1zm-3 3a1 1 0 011 1v1a1 1 0 11-2 0v-1a1 1 0 011-1zm-2-2a1 1 0 10-2 0v3a1 1 0 102 0v-3z" clipRule="evenodd" />
            </svg>
            View Logs
            <svg className="h-4 w-4 ml-auto text-gray-400" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z" clipRule="evenodd" />
            </svg>
          </button>
        </div>

        <div className="bg-white rounded-lg border border-gray-200 shadow-sm">
          <div className="border-b border-gray-200">
            <nav className="flex gap-1 px-4">
              <Tab active onClick={() => setActiveTab('providers')}>
                Providers
              </Tab>
              <Tab active={false} onClick={() => setActiveTab('general')}>
                General
              </Tab>
            </nav>
          </div>

          <div className="p-6">
            <TabPanel>
              <div className="space-y-6">
                <div className="relative">
                  <SearchIcon className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-gray-400 pointer-events-none" />
                  <input
                    ref={searchRef}
                    type="text"
                    value={searchInput}
                    onChange={(e) => setSearchInput(e.target.value)}
                    onKeyDown={handleSearchKeyDown}
                    placeholder="Search providers..."
                    className="w-full pl-10 pr-4 py-2 text-sm border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 placeholder:text-gray-400"
                  />
                  {searchInput && (
                    <button
                      onClick={() => setSearchInput('')}
                      className="absolute right-3 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600"
                    >
                      <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
                        <path d="M6.28 5.22a.75.75 0 00-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 101.06 1.06L10 11.06l3.72 3.72a.75.75 0 101.06-1.06L11.06 10l3.72-3.72a.75.75 0 00-1.06-1.06L10 8.94 6.28 5.22z" />
                      </svg>
                    </button>
                  )}
                </div>

                {isSearching && groups.length === 0 && (
                  <div className="text-center py-8 border border-dashed border-gray-300 rounded-lg">
                    <SearchIcon className="h-8 w-8 text-gray-400 mx-auto mb-2" />
                    <p className="text-sm text-gray-500">No providers found matching your search</p>
                  </div>
                )}

                {!isSearching && allProviders.length === 0 && (
                  <div className="text-center py-8 border border-dashed border-gray-300 rounded-lg">
                    <CloudOffIcon className="h-8 w-8 text-gray-400 mx-auto mb-2" />
                    <p className="text-sm text-gray-500">No providers available.</p>
                  </div>
                )}

                {groups.map((group) => (
                  <ProviderGroup
                    key={group.key}
                    label={group.label}
                    count={group.providers.length}
                    expanded={isSearching || undefined}
                  >
                    {group.providers.map(renderProviderCard)}
                  </ProviderGroup>
                ))}

                <hr className="border-gray-100" />

                <div>
                  <div className="flex items-center justify-between mb-3">
                    <h3 className="text-sm font-medium text-gray-900">Custom</h3>
                    <button
                      onClick={() => setShowCustomProvider(true)}
                      className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium text-white bg-gray-600 hover:bg-gray-700 rounded-md transition-colors"
                    >
                      <CloudIcon className="h-4 w-4" />
                      Add Custom Provider
                    </button>
                  </div>
                  <p className="text-xs text-gray-500">
                    Configure a custom OpenAI-compatible provider by providing the API endpoint and credentials.
                  </p>
                </div>
              </div>
            </TabPanel>
          </div>
        </div>
      </div>

      {confirmRemove && (
        <ConfirmRemoveDialog
          isPending={deleteProvider.isPending}
          onConfirm={handleConfirmDisconnect}
          onCancel={() => setConfirmRemove(null)}
        />
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
    </div>
  )
}
