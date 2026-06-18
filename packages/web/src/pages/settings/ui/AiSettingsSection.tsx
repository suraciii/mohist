import { useEffect, useMemo, useState } from 'react'
import { ChevronRightIcon } from 'lucide-react'
import { useAvailableModelIds, useOpencodeModel, useOpencodeRuntime, useSetStageModels, useStageModels, useUpdateOpencodeModel } from '../../../entities/settings'
import type { Model } from '../../../entities/settings'
import { ModelSelect } from '../../../shared/ui/ModelSelect'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import { SectionState } from './SectionState'
import { SettingsSection } from './SettingsSection'

const STAGES = ['plan', 'build', 'check', 'integrate'] as const
const STAGE_OVERRIDES_ID = 'settings-stage-model-overrides'
const DEFAULT_MODEL_LABEL_ID = 'settings-default-model-label'

export function AiSettingsSection() {
  const { isLoading: runtimeLoading, error: runtimeError } = useOpencodeRuntime()
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
    setStageModels.mutate({ stage, model: modelId })
  }

  const handleClearStageModel = (stage: string) => {
    const updated = { ...localStageModels }
    delete updated[stage]
    setLocalStageModels(updated)
    setStageModels.mutate({ stage, model: null })
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
      <SettingsSection title="External Coder Agent">
        <CardSection>
          <div className="space-y-1.5">
            <div className="flex items-baseline justify-between gap-2">
              <label id={DEFAULT_MODEL_LABEL_ID} className="block text-xs font-medium text-muted-foreground">Default Coder Agent Model</label>
              <span className="text-xs text-muted-foreground">{coderModels.length} models available</span>
            </div>
            <p className="text-xs text-muted-foreground">Passed to opencode when workflow tasks run.</p>
            <ModelSelect
              value={opencodeModelData?.model ?? null}
              placeholder="Opencode default"
              models={coderModels}
              onChange={handleSetOpencodeModel}
              aria-labelledby={DEFAULT_MODEL_LABEL_ID}
            />
          </div>
        </CardSection>
      </SettingsSection>

      <hr className="border" />

      <div>
        <Button
          variant="ghost"
          onClick={() => setStageOverridesOpen(!stageOverridesOpen)}
          aria-expanded={stageOverridesOpen}
          aria-controls={STAGE_OVERRIDES_ID}
          className="flex items-center gap-2 w-full text-left justify-start h-auto px-0 py-0 font-normal hover:bg-transparent"
        >
          <ChevronRightIcon
            className={`h-4 w-4 text-muted-foreground transition-transform ${stageOverridesOpen ? 'rotate-90' : ''}`}
          />
          <span className="text-sm font-medium text-foreground">Stage Model Overrides</span>
          <span className="text-xs text-muted-foreground ml-1">Advanced</span>
        </Button>

        {stageOverridesOpen && (
          <div id={STAGE_OVERRIDES_ID} className="mt-4 space-y-3 pl-6">
            {STAGES.map((stage) => (
              <div key={stage} className="space-y-1">
                <label id={`settings-stage-model-${stage}-label`} className="block text-xs font-medium text-muted-foreground capitalize">{stage}</label>
                <ModelSelect
                  value={localStageModels[stage] ?? null}
                  placeholder="Default"
                  models={coderModels}
                  onChange={(modelId) => handleSetStageModel(stage, modelId)}
                  onClear={() => handleClearStageModel(stage)}
                  allowClear={!!localStageModels[stage]}
                  aria-labelledby={`settings-stage-model-${stage}-label`}
                />
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
