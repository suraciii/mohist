import { useProject } from '../../../entities/project'
import {
  useProjectDefaultWorkflowProfile,
  useSetProjectDefaultWorkflowProfile,
  useAllWorkflowProfiles,
} from '../../../entities/settings'
import { includesWorkflowProfileId, workflowProfileIdEquals } from '../../../entities/settings'
import { CardSection } from '@/shared/ui/components/card-section'
import { Label } from '@/shared/ui/components/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'

export function ProjectDefaultWorkflowControl() {
  const { currentProject } = useProject()
  const { data: profiles, isLoading: profilesLoading, isError: profilesError } = useAllWorkflowProfiles()
  const {
    data: projectProfile,
    isLoading: profileLoading,
    isError,
  } = useProjectDefaultWorkflowProfile()
  const setDefault = useSetProjectDefaultWorkflowProfile()

  if (!currentProject) {
    return (
      <div
        data-testid="project-default-workflow-no-project"
        className="text-sm text-muted-foreground"
      >
        No project selected
      </div>
    )
  }

  const configuredTemplateId = projectProfile?.defaultTemplateId ?? null
  const disabledIds = projectProfile?.disabledWorkflowProfileIds ?? []
  const inheritedDefaultId = profiles?.find((p) => p.isDefault && !includesWorkflowProfileId(disabledIds, p.id))?.id
    ?? profiles?.find((p) => !includesWorkflowProfileId(disabledIds, p.id))?.id
    ?? 'none'
  const isConfiguredInCatalog = configuredTemplateId
    ? profiles?.some((p) => workflowProfileIdEquals(p.id, configuredTemplateId)) ?? true
    : true
  const isDefaultDisabled = configuredTemplateId
    ? includesWorkflowProfileId(disabledIds, configuredTemplateId)
    : false
  const isLoading = profileLoading || profilesLoading

  function handleValueChange(value: string | null) {
    if (value) {
      setDefault.mutate({ templateId: value })
    }
  }

  return (
    <CardSection title="Project default workflow" titleAs="h3" tone="blue">
      {isLoading ? (
        <div className="text-sm text-muted-foreground">Loading...</div>
      ) : isError || profilesError ? (
        <div className="text-sm text-red-700">Failed to load project default workflow.</div>
      ) : (
        <div className="space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            {configuredTemplateId ? (
              <>
                <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-green-50 text-green-700 border border-green-200">
                  Project default
                </span>
                <span
                  className="text-sm font-medium text-foreground"
                  data-testid="project-default-workflow-value"
                >
                  {configuredTemplateId}
                </span>
              </>
            ) : (
              <span className="text-sm text-muted-foreground">
                No project default configured. New issues inherit the system default (
                <span
                  className="font-mono text-xs"
                  data-testid="project-default-workflow-system-default"
                >
                  {inheritedDefaultId}
                </span>
                ).
              </span>
            )}
          </div>

          {configuredTemplateId && !isConfiguredInCatalog && (
            <div
              data-testid="project-default-workflow-orphan-warning"
              className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800"
            >
              Configured default{' '}
              <span className="font-mono">{configuredTemplateId}</span> is not available in the
              system catalog.
            </div>
          )}

          {isDefaultDisabled && (
            <div
              data-testid="project-default-workflow-disabled-warning"
              className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800"
            >
              The current project default{' '}
              <span className="font-mono">{configuredTemplateId}</span> is currently disabled. New
              issues will fall through to the first enabled system profile.
            </div>
          )}

          <div className="flex items-end gap-2">
            <div className="flex-1 min-w-0">
              <Label className="text-xs" htmlFor="project-default-workflow-select">
                Default workflow
              </Label>
              <Select
                value={configuredTemplateId ?? ''}
                onValueChange={handleValueChange}
                disabled={setDefault.isPending}
              >
                <SelectTrigger
                  id="project-default-workflow-select"
                  data-testid="project-default-workflow-select"
                  className="w-full max-w-full"
                >
                  <SelectValue placeholder="Select workflow" />
                </SelectTrigger>
                <SelectContent>
                  {profiles?.map((profile) => {
                    const isDisabled = includesWorkflowProfileId(disabledIds, profile.id)
                    return (
                      <SelectItem
                        key={profile.id}
                        value={profile.id}
                        disabled={isDisabled}
                      >
                        <span className={isDisabled ? 'text-muted-foreground' : ''}>
                          {profile.displayName}
                        </span>
                        <span className="text-muted-foreground/60">
                          {' '}({profile.id})
                        </span>
                      </SelectItem>
                    )
                  })}
                </SelectContent>
              </Select>
            </div>
          </div>
        </div>
      )}
    </CardSection>
  )
}
