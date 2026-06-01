import { useEffect, useMemo, useState } from 'react'
import { ChevronRightIcon } from 'lucide-react'
import { useAvailableModelIds, useOpencodeModel, useOpencodeRuntime, useSetStageModels, useStageModels, useUpdateOpencodeModel } from '../../../entities/settings'
import type { Model } from '../../../entities/settings'
import { ModelSelect } from '../../../shared/ui/ModelSelect'
import { Button } from '@/shared/ui/components/button'
import { SectionState } from './SectionState'

const STAGES = ['plan', 'build', 'check', 'integrate'] as const

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
    return <SectionState variant="loading" title="Coder Agent & Models" skeletonRows={2} />
  }

  const error = runtimeError ?? modelsError
  if (error) {
    return (
      <SectionState
        variant="error"
        title="Coder Agent & Models"
        message={`Failed to load opencode runtime: ${(error as Error).message}`}
      />
    )
  }

  return (
    <div className="space-y-8">
      <div className="space-y-4">
        <h3 className="text-sm font-medium text-foreground">External Coder Agent</h3>

        <div className="rounded-md border bg-muted px-3 py-2">
          <div className="grid grid-cols-1 gap-2 text-xs sm:grid-cols-3">
            <div>
              <div className="text-muted-foreground">Runtime</div>
              <div className="font-mono text-foreground">{runtime?.mode ?? 'local-opencode'}</div>
            </div>
            <div>
              <div className="text-muted-foreground">Command</div>
              <div className="font-mono text-foreground">{runtime?.command ?? 'opencode'}</div>
            </div>
            <div>
              <div className="text-muted-foreground">Models</div>
              <div className="font-mono text-foreground">{coderModels.length}</div>
            </div>
          </div>
          <p className="mt-2 text-xs text-muted-foreground">
            Mohist does not configure AI providers. It delegates coder work to the connected opencode runner.
          </p>
        </div>

        <div className="space-y-1.5">
          <label className="block text-xs font-medium text-foreground/80">Default Coder Agent Model</label>
          <p className="text-xs text-muted-foreground">Passed to opencode when workflow tasks run.</p>
          <ModelSelect
            value={opencodeModelData?.model ?? null}
            placeholder="Opencode default"
            models={coderModels}
            onChange={handleSetOpencodeModel}
          />
        </div>
      </div>

      <hr className="border" />

      <div>
        <Button
          variant="ghost"
          onClick={() => setStageOverridesOpen(!stageOverridesOpen)}
          className="flex items-center gap-2 w-full text-left justify-start h-auto px-0 py-0 font-normal hover:bg-transparent"
        >
          <ChevronRightIcon
            className={`h-4 w-4 text-muted-foreground/70 transition-transform ${stageOverridesOpen ? 'rotate-90' : ''}`}
          />
          <span className="text-sm font-medium text-foreground">Stage Model Overrides</span>
          <span className="text-xs text-muted-foreground/70 ml-1">Advanced</span>
        </Button>

        {stageOverridesOpen && (
          <div className="mt-4 space-y-3 pl-6">
            {STAGES.map((stage) => (
              <div key={stage} className="space-y-1">
                <label className="block text-xs font-medium text-muted-foreground capitalize">{stage}</label>
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
