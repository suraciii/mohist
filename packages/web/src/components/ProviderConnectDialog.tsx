import { useQueryClient } from '@tanstack/react-query'
import { Dialog } from './Dialog'
import { useSaveProvider } from '../hooks/useQueries'
import type { Provider } from '../lib/provider-api'

interface Props {
  open: boolean
  onClose: () => void
  provider: Provider | null
}

function providerDescriptions(providerId: string): string {
  const descriptions: Record<string, string> = {
    anthropic: 'Add Anthropic models to the Mohist model catalog.',
    openai: 'Add OpenAI models to the Mohist model catalog.',
    glm: 'Add Zhipu GLM models to the Mohist model catalog.',
    kimi: 'Add Moonshot Kimi models to the Mohist model catalog.',
    minimax: 'Add MiniMax models to the Mohist model catalog.',
    deepseek: 'Add DeepSeek models to the Mohist model catalog.',
    qwen: 'Add Qwen models to the Mohist model catalog.',
  }
  return descriptions[providerId] || 'Add this provider to the Mohist model catalog.'
}

export function ProviderConnectDialog({ open, onClose, provider }: Props) {
  const queryClient = useQueryClient()
  const saveProvider = useSaveProvider()

  const handleClose = () => {
    onClose()
  }

  const handleSave = () => {
    if (!provider) return
    saveProvider.mutate(
      { id: provider.id, data: { name: provider.name } },
      {
        onSuccess: () => {
          queryClient.invalidateQueries({ queryKey: ['providers'] })
          queryClient.invalidateQueries({ queryKey: ['models'] })
          handleClose()
        },
      },
    )
  }

  if (!provider) return null

  return (
    <Dialog open={open} onClose={handleClose} title={`Add ${provider.name}`}>
      <div className="space-y-4">
        <p className="text-sm text-gray-600">
          {providerDescriptions(provider.id)}
        </p>

        <div className="rounded-md border border-blue-100 bg-blue-50 px-3 py-2 text-xs text-blue-700">
          Mohist does not authenticate with providers. Configure provider credentials in your external coder agent, such as opencode.
        </div>

        {saveProvider.error && (
          <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
            {(saveProvider.error as Error).message}
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
            onClick={handleSave}
            disabled={saveProvider.isPending}
            className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
          >
            {saveProvider.isPending ? 'Adding...' : 'Add to Catalog'}
          </button>
        </div>
      </div>
    </Dialog>
  )
}
