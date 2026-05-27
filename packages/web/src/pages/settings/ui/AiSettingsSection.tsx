import { useEffect, useMemo, useState } from 'react'
import { useAvailableModelIds, useOpencodeModel, useOpencodeRuntime, useSetStageModels, useStageModels, useUpdateOpencodeModel } from '../../../entities/settings/api/queries'
import type { Model } from '../../../shared/api/types'
import { ModelSelect } from '../../../shared/ui/ModelSelect'

const STAGES = ['plan', 'build', 'check', 'integrate'] as const

function ChevronRightIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path fillRule="evenodd" d="M7.21 8.145a.75.75 0 011.06-.02L10 9.835l1.73-1.71a.75.75 0 011.04 1.08l-2.25 2.22a.75.75 0 01-1.04 0l-2.25-2.22a.75.75 0 01-.02-1.06z" clipRule="evenodd" />
    </svg>
  )
}

export function AiSettingsSection() {
  const { data: runtime, isLoading: runtimeLoading, error: runtimeError } = useOpencodeRuntime()
  const { data: availableModelIds, isLoading: modelsLoading, error: modelsError } = useAvailableModelIds()
  const { data: opencodeModelData } = useOpencodeModel()
  const setOpencodeModel = useUpdateOpencodeModel()
  const { data: stageModelsData } = useStageModels()
  const setStageModels = useSetStageModels()
  const [stageOverridesOpen, setStageOverridesOpen] = useState(false)
  const [localStageModels, setLocalStageModels] = useState<Record<string, string>>({})

  useEffect(() => {
    if (stageModelsData?.stageModels) setLocalStageModels(stageModelsData.stageModels)
  }, [stageModelsData])

  const coderModels = useMemo(() => {
    return (availableModelIds ?? [])
      .map((id): Model => ({ id, name: id.split('/').pop() || id, badges: [], contextWindow: 0 }))
      .sort((a, b) => a.id.localeCompare(b.id))
  }, [availableModelIds])

  const handleSetOpencodeModel = (modelId: string) => {
    setOpencodeModel.mutate(modelId)
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

  if (runtimeLoading || modelsLoading) {
    return (
      <div className="space-y-4">
        <h3 className="text-sm font-medium text-gray-900">Coder Agent & Models</h3>
        <div className="space-y-3">
          {[1, 2].map((i) => (
            <div key={i} className="h-16 bg-gray-100 rounded-lg animate-pulse" />
          ))}
        </div>
      </div>
    )
  }

  const error = runtimeError ?? modelsError
  if (error) {
    return (
      <div className="space-y-4">
        <h3 className="text-sm font-medium text-gray-900">Coder Agent & Models</h3>
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          Failed to load opencode runtime: {(error as Error).message}
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-8">
      <div className="space-y-4">
        <h3 className="text-sm font-medium text-gray-900">External Coder Agent</h3>

        <div className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2">
          <div className="grid grid-cols-1 gap-2 text-xs sm:grid-cols-3">
            <div>
              <div className="text-gray-500">Runtime</div>
              <div className="font-mono text-gray-900">{runtime?.mode ?? 'local-opencode'}</div>
            </div>
            <div>
              <div className="text-gray-500">Command</div>
              <div className="font-mono text-gray-900">{runtime?.command ?? 'opencode'}</div>
            </div>
            <div>
              <div className="text-gray-500">Models</div>
              <div className="font-mono text-gray-900">{coderModels.length}</div>
            </div>
          </div>
          <p className="mt-2 text-xs text-gray-500">
            Mohist does not configure AI providers. It delegates coder work to the connected opencode runner.
          </p>
        </div>

        <div className="space-y-1.5">
          <label className="block text-xs font-medium text-gray-700">Default Coder Agent Model</label>
          <p className="text-xs text-gray-500">Passed to opencode when workflow tasks run.</p>
          <ModelSelect
            value={opencodeModelData?.model ?? null}
            placeholder="Opencode default"
            models={coderModels}
            onChange={handleSetOpencodeModel}
          />
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
                <label className="block text-xs font-medium text-gray-600 capitalize">{stage}</label>
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
    </div>
  )
}
