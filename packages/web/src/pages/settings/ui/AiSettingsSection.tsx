import { useEffect, useMemo, useState } from 'react'
import { ChevronRightIcon } from 'lucide-react'
import { getWorkflowProfileAgentRuntime, resolveEffectiveDefaultWorkflowProfile, useAvailableModelIds, useOpencodeModel, useProjectDefaultWorkflowProfile, useSetStageModels, useStageModels, useUpdateOpencodeModel, useWorkflowProfiles } from '../../../entities/settings'
import type { Model } from '../../../entities/settings'
import { ModelSelect } from '../../../shared/ui/ModelSelect'
import { resolveVariantAgainstModel } from '../../../shared/ui/model-variants'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import type { SettingsSearchEntry } from '../model/settings-search'
import { getSectionMeta } from '../lib/sections'
import { SectionState } from './SectionState'
import { SettingsSection } from './SettingsSection'

const STAGES = ['plan', 'build', 'check', 'integrate'] as const
const STAGE_OVERRIDES_ID = 'settings-stage-model-overrides'
export const REVEAL_STAGE_MODEL_OVERRIDES_EVENT = 'mohist:settings:reveal-stage-model-overrides'
const DEFAULT_MODEL_LABEL_ID = 'settings-default-model-label'

export const AI_SETTINGS_DESCRIPTORS: SettingsSearchEntry[] = [
  {
    tab: 'ai',
    label: 'Default Coder Agent Model',
    description: 'Passed to opencode when workflow tasks run.',
    placeholder: 'Opencode default',
    focusTargetId: 'settings-default-model',
  },
  ...STAGES.map(
    (stage): SettingsSearchEntry => ({
      tab: 'ai',
      label: `${stage} stage model`,
      description: `Override the coder agent model used for the ${stage} stage.`,
      placeholder: 'Default',
      focusTargetId: `settings-stage-model-${stage}`,
      revealEvent: REVEAL_STAGE_MODEL_OVERRIDES_EVENT,
    }),
  ),
]

export function AiSettingsSection() {
  const { data: workflowProfiles, isLoading: profilesLoading, error: profilesError } = useWorkflowProfiles()
  const { data: projectWorkflowProfile, isLoading: defaultProfileLoading, error: defaultProfileError } = useProjectDefaultWorkflowProfile()
  const { effectiveTemplateId } = resolveEffectiveDefaultWorkflowProfile(projectWorkflowProfile, workflowProfiles)
  const selectedRuntime = profilesLoading || defaultProfileLoading
    ? null
    : getWorkflowProfileAgentRuntime(workflowProfiles, effectiveTemplateId)
  const { data: availableModelIds, isLoading: modelsLoading, error: modelsError } = useAvailableModelIds(selectedRuntime)
  const { data: opencodeModelData } = useOpencodeModel()
  const setOpencodeModel = useUpdateOpencodeModel()
  const { data: stageModelsData } = useStageModels()
  const setStageModels = useSetStageModels()
  const [stageOverridesOpen, setStageOverridesOpen] = useState(false)
  const [localStageModels, setLocalStageModels] = useState<Record<string, string>>({})
  const [localStageModelVariants, setLocalStageModelVariants] = useState<Record<string, string>>({})
  const { label: sectionLabel } = getSectionMeta('ai')

  useEffect(() => {
    if (stageModelsData?.stageModels) setLocalStageModels(stageModelsData.stageModels)
    if (stageModelsData?.stageModelVariants) setLocalStageModelVariants(stageModelsData.stageModelVariants)
    else setLocalStageModelVariants({})
  }, [stageModelsData])

  useEffect(() => {
    function revealStageOverrides() {
      setStageOverridesOpen(true)
    }
    window.addEventListener(REVEAL_STAGE_MODEL_OVERRIDES_EVENT, revealStageOverrides)
    return () => window.removeEventListener(REVEAL_STAGE_MODEL_OVERRIDES_EVENT, revealStageOverrides)
  }, [])

  const modelIds = availableModelIds?.models ?? []
  const modelVariantsMap = availableModelIds?.modelVariants ?? {}

  const coderModels = useMemo(() => {
    return modelIds
      .map((id): Model => ({ id, name: id.split('/').pop() || id, badges: [], contextWindow: 0 }))
      .sort((a, b) => a.id.localeCompare(b.id))
  }, [modelIds])

  const storedDefaultModel = opencodeModelData?.model ?? null
  const storedDefaultVariant = opencodeModelData?.variant ?? null
  const hasSelectedRuntime = selectedRuntime !== null
  const configuredStageEntries = STAGES.filter((stage) => !!localStageModels[stage])
  const displayModelVariants = useMemo(() => {
    if (hasSelectedRuntime) return modelVariantsMap

    const variants: Record<string, string[]> = {}
    if (storedDefaultModel && storedDefaultVariant) variants[storedDefaultModel] = [storedDefaultVariant]
    for (const stage of configuredStageEntries) {
      const model = localStageModels[stage]
      const variant = localStageModelVariants[stage]
      if (model && variant) variants[model] = [...(variants[model] ?? []), variant]
    }
    return variants
  }, [configuredStageEntries, hasSelectedRuntime, localStageModelVariants, localStageModels, modelVariantsMap, storedDefaultModel, storedDefaultVariant])
  const resolvedDefaultVariant = resolveVariantAgainstModel(storedDefaultModel, storedDefaultVariant, displayModelVariants)

  const handleSetOpencodeModel = (modelId: string) => {
    setOpencodeModel.mutate({ model: modelId, variant: null })
  }

  const handleClearOpencodeModel = () => {
    setOpencodeModel.mutate({ model: null, variant: null })
  }

  const handleSetDefaultVariant = (modelId: string, variant: string | null) => {
    setOpencodeModel.mutate({ model: modelId, variant })
  }

  const handleSetStageModel = (stage: string, modelId: string) => {
    const updated = { ...localStageModels, [stage]: modelId }
    setLocalStageModels(updated)
    setLocalStageModelVariants((prev) => {
      const next = { ...prev }
      delete next[stage]
      return next
    })
    setStageModels.mutate({ stage, model: modelId, variant: null })
  }

  const handleClearStageModel = (stage: string) => {
    const updated = { ...localStageModels }
    delete updated[stage]
    setLocalStageModels(updated)
    setLocalStageModelVariants((prev) => {
      const next = { ...prev }
      delete next[stage]
      return next
    })
    setStageModels.mutate({ stage, model: null, variant: null })
  }

  const handleSetStageVariant = (stage: string, variant: string | null, selectedModel?: string | null) => {
    const stageModel = selectedModel ?? localStageModels[stage] ?? null
    if (!stageModel) return
    setLocalStageModelVariants((prev) => {
      const next = { ...prev }
      if (variant) next[stage] = variant
      else delete next[stage]
      return next
    })
    setStageModels.mutate({ stage, model: stageModel, variant })
  }

  if (profilesLoading || defaultProfileLoading || modelsLoading) {
    return <SectionState variant="loading" title={sectionLabel} skeletonRows={2} />
  }

  const error = profilesError ?? defaultProfileError ?? modelsError
  if (error) {
    return (
      <SectionState
        variant="error"
        title={sectionLabel}
        message={`Failed to load workflow profile models: ${(error as Error).message}`}
      />
    )
  }

  return (
    <div className="space-y-8">
      {(hasSelectedRuntime || storedDefaultModel) && (
        <SettingsSection title={sectionLabel}>
          <CardSection>
            <div className="space-y-1.5">
              <div className="flex items-baseline justify-between gap-2">
                <label id={DEFAULT_MODEL_LABEL_ID} className="block text-xs font-medium text-muted-foreground">Default Coder Agent Model</label>
                {hasSelectedRuntime && (
                  <span className="text-xs text-muted-foreground">{coderModels.length} models available</span>
                )}
              </div>
              <p className="text-xs text-muted-foreground">Passed to the selected workflow profile runtime when tasks run.</p>
              <ModelSelect
                id="settings-default-model"
                value={storedDefaultModel}
                placeholder="Runtime default"
                models={hasSelectedRuntime ? coderModels : []}
                onChange={handleSetOpencodeModel}
                onClear={hasSelectedRuntime && storedDefaultModel ? handleClearOpencodeModel : undefined}
                allowClear={hasSelectedRuntime && !!storedDefaultModel}
                aria-labelledby={DEFAULT_MODEL_LABEL_ID}
                modelVariants={displayModelVariants}
                valueVariant={resolvedDefaultVariant}
                onChangeModelVariant={handleSetDefaultVariant}
                disabled={!hasSelectedRuntime}
              />
            </div>
          </CardSection>
        </SettingsSection>
      )}

      {((hasSelectedRuntime || configuredStageEntries.length > 0) && (
        <>
          {storedDefaultModel || hasSelectedRuntime ? <hr className="border" /> : null}

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
                {(hasSelectedRuntime ? STAGES : configuredStageEntries).map((stage) => {
                  const stageModel = localStageModels[stage] ?? null
                  const stageVariant = resolveVariantAgainstModel(stageModel, localStageModelVariants[stage], displayModelVariants)
                  return (
                    <div key={stage} className="space-y-1">
                      <label id={`settings-stage-model-${stage}-label`} className="block text-xs font-medium text-muted-foreground capitalize">{stage}</label>
                      <ModelSelect
                        id={`settings-stage-model-${stage}`}
                        value={stageModel}
                        placeholder="Default"
                        models={hasSelectedRuntime ? coderModels : []}
                        onChange={(modelId) => handleSetStageModel(stage, modelId)}
                        onClear={hasSelectedRuntime ? () => handleClearStageModel(stage) : undefined}
                        allowClear={hasSelectedRuntime && !!stageModel}
                        aria-labelledby={`settings-stage-model-${stage}-label`}
                        size="compact"
                        modelVariants={displayModelVariants}
                        valueVariant={stageVariant}
                        onChangeModelVariant={(modelId, variant) => handleSetStageVariant(stage, variant, modelId)}
                        disabled={!hasSelectedRuntime}
                      />
                    </div>
                  )
                })}
              </div>
            )}
          </div>
        </>
      ))}
    </div>
  )
}
