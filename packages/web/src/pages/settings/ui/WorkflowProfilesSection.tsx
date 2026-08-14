import { useCallback, useLayoutEffect, useRef, useState, type ComponentType } from 'react'
import { ArrowLeftIcon } from 'lucide-react'
import { useProject } from '../../../entities/project'
import {
  selectAgentTurnActions,
  useActionCatalog,
  useDisableWorkflowProfile,
  useEnableWorkflowProfile,
  useProjectDefaultWorkflowProfile,
  useSetWorkflowProfileAgentAction,
  useWorkflowProfile,
  useAllWorkflowProfiles,
} from '../../../entities/settings'
import type {
  ActionCatalog,
  ProjectDefaultWorkflowProfile,
  WorkflowProfileDetail,
  WorkflowProfileInfo,
} from '../../../entities/settings'
import { includesWorkflowProfileId } from '../../../entities/settings'
import { CardSection } from '../../../shared/ui/components/card-section'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '../../../shared/ui/components/select'
import { Switch } from '../../../shared/ui/components/switch'
import type { SettingsSearchEntry } from '../model/settings-search'
import { getSectionMeta } from '../lib/sections'
import { NoProjectCard } from './NoProjectCard'
import { ProjectDefaultWorkflowControl } from './ProjectDefaultWorkflowControl'
import { SectionState } from './SectionState'
import { SettingsSection } from './SettingsSection'

export const WORKFLOW_DESCRIPTORS: SettingsSearchEntry[] = [
  {
    tab: 'workflows',
    label: 'Workflow Profiles',
    description: 'Browse and manage system workflow profiles.',
    focusTargetId: 'workflow-profiles-section',
  },
  {
    tab: 'workflows',
    label: 'Project Default Workflow',
    description: 'Set the default workflow profile for new issues in this project.',
    focusTargetId: 'project-default-workflow',
  },
]

function YamlViewer({ yaml }: { yaml: string }) {
  return (
    <pre className="text-xs font-mono leading-relaxed text-foreground whitespace-pre-wrap break-all max-h-[600px] overflow-auto">
      {yaml}
    </pre>
  )
}

function StageSummary({
  stage,
}: {
  stage: { stage: string; requiresApproval: boolean; tasks: string[]; checks: string[] }
}) {
  return (
    <div className="flex items-start gap-3 py-2 border-b last:border-b-0">
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-2">
          <span className="text-xs font-medium text-foreground capitalize">{stage.stage}</span>
          {stage.requiresApproval && (
            <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-blue-50 text-blue-700 border border-blue-200">
              approval
            </span>
          )}
        </div>
        <div className="flex gap-3 mt-1">
          {stage.tasks.length > 0 && (
            <span className="text-[11px] text-muted-foreground">
              {stage.tasks.length} task{stage.tasks.length !== 1 ? 's' : ''}
            </span>
          )}
          {stage.checks.length > 0 && (
            <span className="text-[11px] text-muted-foreground">
              {stage.checks.length} check{stage.checks.length !== 1 ? 's' : ''}
            </span>
          )}
        </div>
      </div>
    </div>
  )
}

export type WorkflowProfileHook = (profileId: string | null) => {
  data: WorkflowProfileDetail | undefined
  isLoading: boolean
  isError: boolean
}

export type WorkflowActionCatalogHook = () => {
  data: ActionCatalog | undefined
  isLoading: boolean
  isError: boolean
}

export type WorkflowProfileAgentActionMutationHook = () => {
  mutate: (variables: { profileId: string; agentAction: string | null }) => void
  isPending: boolean
  error: Error | null
}

interface WorkflowProfileMutation {
  mutate: (profileId: string) => void
  isPending: boolean
}

export interface WorkflowProfilesSectionData {
  allProfiles: WorkflowProfileInfo[] | undefined
  profilesLoading: boolean
  profilesError: boolean
  projectProfile: ProjectDefaultWorkflowProfile | undefined
  projectProfileLoading: boolean
  projectProfileError: boolean
  disableMutation: WorkflowProfileMutation
  enableMutation: WorkflowProfileMutation
}

export type WorkflowProfilesSectionDataHook = () => WorkflowProfilesSectionData

export interface WorkflowProfilesSectionComponents {
  ProjectDefaultWorkflowControl: ComponentType
}

const useDefaultData: WorkflowProfilesSectionDataHook = () => {
  const { data: allProfiles, isLoading: profilesLoading, isError: profilesError } = useAllWorkflowProfiles()
  const {
    data: projectProfile,
    isLoading: projectProfileLoading,
    isError: projectProfileError,
  } = useProjectDefaultWorkflowProfile()
  return {
    allProfiles,
    profilesLoading,
    profilesError,
    projectProfile,
    projectProfileLoading,
    projectProfileError,
    disableMutation: useDisableWorkflowProfile(),
    enableMutation: useEnableWorkflowProfile(),
  }
}

const PROFILE_DEFAULT_ACTION = '__profile_default__'

function AgentActionSelector({
  profileId,
  agentAction,
  actionCatalogHook,
  agentActionMutationHook,
}: {
  profileId: string
  agentAction: string
  actionCatalogHook: WorkflowActionCatalogHook
  agentActionMutationHook: WorkflowProfileAgentActionMutationHook
}) {
  const actionCatalog = actionCatalogHook()
  const agentActionMutation = agentActionMutationHook()
  const agentActions = selectAgentTurnActions(actionCatalog.data)

  return (
    <div className="space-y-2 border-t border-border/60 pt-4">
      <label id="workflow-profile-agent-action-label" className="text-sm font-medium text-foreground">
        Agent Action
      </label>
      <Select
        value={agentAction}
        onValueChange={(value) => {
          if (!value) return
          agentActionMutation.mutate({
            profileId,
            agentAction: value === PROFILE_DEFAULT_ACTION ? null : value,
          })
        }}
        disabled={
          actionCatalog.isLoading || actionCatalog.isError || agentActions.length === 0 || agentActionMutation.isPending
        }
      >
        <SelectTrigger
          aria-labelledby="workflow-profile-agent-action-label"
          className="w-full max-w-sm"
          data-testid="workflow-profile-agent-action-selector"
        >
          <SelectValue placeholder="Select Agent Action" />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={PROFILE_DEFAULT_ACTION}>Profile default</SelectItem>
          {agentActions.map((action) => (
            <SelectItem key={action.name} value={action.name}>
              {action.name}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      {actionCatalog.isError && <p className="text-xs text-destructive">Failed to load Agent Actions.</p>}
      {!actionCatalog.isLoading && !actionCatalog.isError && agentActions.length === 0 && (
        <p className="text-xs text-muted-foreground">No Agent Actions are available.</p>
      )}
      {agentActionMutation.error && <p className="text-xs text-destructive">{agentActionMutation.error.message}</p>}
    </div>
  )
}

function ProfileDetail({
  profileId,
  onBack,
  profileHook,
  actionCatalogHook,
  agentActionMutationHook,
}: {
  profileId: string
  onBack: () => void
  profileHook: WorkflowProfileHook
  actionCatalogHook: WorkflowActionCatalogHook
  agentActionMutationHook: WorkflowProfileAgentActionMutationHook
}) {
  const { data: profile, isLoading, isError } = profileHook(profileId)
  const { label: sectionLabel } = getSectionMeta('workflows')

  if (isLoading) {
    return (
      <div className="space-y-4">
        <div className="h-4 w-32 bg-muted rounded animate-pulse" />
        <div className="h-40 bg-muted rounded-md animate-pulse" />
      </div>
    )
  }

  if (isError || !profile) {
    return <div className="text-sm text-red-700">Failed to load profile.</div>
  }

  return (
    <SettingsSection title={sectionLabel}>
      <div>
        <button
          onClick={onBack}
          data-testid="workflow-profile-back"
          className="mb-2 inline-flex min-h-11 items-center gap-1 px-3 py-2 text-xs text-muted-foreground transition-colors hover:text-foreground"
        >
          <ArrowLeftIcon className="w-3 h-3" />
          All profiles
        </button>
        <div className="flex items-center gap-2">
          <h3 className="text-sm font-medium text-foreground">{profile.displayName}</h3>
          {profile.isDefault && (
            <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-slate-50 text-slate-700 border border-slate-200">
              System default
            </span>
          )}
        </div>
        <p className="text-[10px] text-muted-foreground mt-1 font-mono">{profile.id}</p>
        <p
          data-testid="workflow-profile-description"
          className="text-sm text-foreground mt-3 leading-relaxed whitespace-pre-line"
        >
          {profile.description}
        </p>
      </div>

      {profile.agentAction != null && (
        <AgentActionSelector
          profileId={profile.id}
          agentAction={profile.agentAction}
          actionCatalogHook={actionCatalogHook}
          agentActionMutationHook={agentActionMutationHook}
        />
      )}

      <CardSection title="Stages" titleAs="h3" className="py-1">
        <div>
          {profile.stages.map((s) => (
            <StageSummary key={s.stage} stage={s} />
          ))}
        </div>
      </CardSection>

      <CardSection title="Shared Stage Definition (YAML)" titleAs="h3">
        <p className="text-[11px] text-muted-foreground mb-3">
          quick-fix and experiment reuse these stages from mohist/local; only the metadata above differs.
        </p>
        <YamlViewer yaml={profile.yaml} />
      </CardSection>
    </SettingsSection>
  )
}

function ProfileCard({
  profile,
  onClick,
  isDisabled,
  isLastEnabled,
  toggleDisabled,
  onToggleDisabled,
  profileHook,
}: {
  profile: WorkflowProfileInfo
  onClick: () => void
  isDisabled: boolean
  isLastEnabled: boolean
  toggleDisabled: boolean
  onToggleDisabled: (profileId: string, currentlyDisabled: boolean) => void
  profileHook: WorkflowProfileHook
}) {
  const descriptionRef = useRef<HTMLParagraphElement>(null)
  const [isExpanded, setIsExpanded] = useState(false)
  const [hasOverflow, setHasOverflow] = useState(false)
  const [blockedMessage, setBlockedMessage] = useState<string | null>(null)
  const { data: detail, isLoading: detailLoading } = profileHook(profile.id)

  useLayoutEffect(() => {
    const description = descriptionRef.current
    if (!description) return

    setHasOverflow(description.scrollHeight > description.clientHeight)
  }, [profile.description])

  const stages = detail?.stages ?? []

  const handleToggle = useCallback(() => {
    if (!isDisabled && isLastEnabled) {
      setBlockedMessage('At least one workflow profile must remain enabled.')
      return
    }
    setBlockedMessage(null)
    onToggleDisabled(profile.id, isDisabled)
  }, [isDisabled, isLastEnabled, profile.id, onToggleDisabled])

  return (
    <CardSection className="p-0 hover:bg-muted/50 transition-colors">
      <div data-testid={`workflow-profile-${profile.id}`} className="p-4">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium text-foreground">{profile.displayName}</span>
              {profile.isDefault && (
                <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-slate-50 text-slate-700 border border-slate-200">
                  System default
                </span>
              )}
            </div>
            <p className="text-[10px] text-muted-foreground/70 mt-1 font-mono">{profile.id}</p>
          </div>
          <div className="flex items-center gap-2">
            {profile.isBuiltIn === true && (
              <Switch
                aria-label={`${isDisabled ? 'Enable' : 'Disable'} workflow profile ${profile.displayName}`}
                checked={!isDisabled}
                onCheckedChange={handleToggle}
                disabled={toggleDisabled}
              />
            )}
            <button
              type="button"
              onClick={onClick}
              className="shrink-0 text-xs font-medium text-primary hover:text-primary/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 rounded-sm"
            >
              View details
            </button>
          </div>
        </div>
        <p
          ref={descriptionRef}
          data-testid={`workflow-profile-${profile.id}-description`}
          className={`text-xs text-muted-foreground mt-2 leading-relaxed whitespace-pre-line ${isExpanded ? '' : 'line-clamp-2'}`}
        >
          {profile.description}
        </p>
        {hasOverflow && !isExpanded && (
          <button
            type="button"
            onClick={(event) => {
              event.stopPropagation()
              setIsExpanded(true)
            }}
            className="mt-1 inline-flex text-[11px] font-medium text-primary hover:text-primary/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 rounded-sm"
          >
            Read more
          </button>
        )}
        <div className="mt-3 flex flex-wrap gap-1.5" aria-label="Workflow stages">
          {detailLoading ? (
            <span className="inline-flex items-center rounded-full border bg-muted/50 px-2 py-0.5 text-[10px] font-medium text-muted-foreground animate-pulse">
              Loading...
            </span>
          ) : stages.length === 0 ? (
            <span className="inline-flex items-center rounded-full border bg-muted/50 px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
              No stages
            </span>
          ) : (
            stages.map((s) => (
              <span
                key={s.stage}
                className="inline-flex items-center rounded-full border bg-muted/50 px-2 py-0.5 text-[10px] font-medium text-muted-foreground"
              >
                {s.stage}
              </span>
            ))
          )}
        </div>
        {blockedMessage && (
          <div
            data-testid={`workflow-profile-${profile.id}-blocked`}
            className="mt-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800"
          >
            {blockedMessage}
          </div>
        )}
      </div>
    </CardSection>
  )
}

export function WorkflowProfilesSection({
  dataHook = useDefaultData,
  profileHook = useWorkflowProfile,
  actionCatalogHook = useActionCatalog,
  agentActionMutationHook = useSetWorkflowProfileAgentAction,
  components,
}: {
  dataHook?: WorkflowProfilesSectionDataHook
  profileHook?: WorkflowProfileHook
  actionCatalogHook?: WorkflowActionCatalogHook
  agentActionMutationHook?: WorkflowProfileAgentActionMutationHook
  components?: Partial<WorkflowProfilesSectionComponents>
} = {}) {
  const { currentProject } = useProject()
  const {
    allProfiles,
    profilesLoading,
    profilesError,
    projectProfile,
    projectProfileLoading,
    projectProfileError,
    disableMutation,
    enableMutation,
  } = dataHook()
  const DefaultWorkflowControl = components?.ProjectDefaultWorkflowControl ?? ProjectDefaultWorkflowControl
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const { label: sectionLabel, description: sectionDescription } = getSectionMeta('workflows')

  const disabledIds = projectProfile?.disabledWorkflowProfileIds ?? []
  const builtInProfiles = allProfiles?.filter((p) => p.isBuiltIn === true) ?? []
  const enabledCount = builtInProfiles.filter((p) => !includesWorkflowProfileId(disabledIds, p.id)).length

  const handleToggleDisabled = useCallback(
    (profileId: string, currentlyDisabled: boolean) => {
      if (currentlyDisabled) {
        enableMutation.mutate(profileId)
      } else {
        disableMutation.mutate(profileId)
      }
    },
    [enableMutation, disableMutation],
  )

  if (!currentProject) {
    return <NoProjectCard title={sectionLabel} />
  }

  if (selectedId) {
    return (
      <ProfileDetail
        profileId={selectedId}
        onBack={() => setSelectedId(null)}
        profileHook={profileHook}
        actionCatalogHook={actionCatalogHook}
        agentActionMutationHook={agentActionMutationHook}
      />
    )
  }

  if (profilesLoading || projectProfileLoading) {
    return <SectionState variant="loading" title={sectionLabel} skeletonRows={2} />
  }

  if (profilesError || projectProfileError || !allProfiles || !projectProfile) {
    return <SectionState variant="error" title={sectionLabel} message="Failed to load workflow profile settings." />
  }

  return (
    <div id="workflow-profiles-section" tabIndex={-1}>
      <SettingsSection title={sectionLabel} description={sectionDescription}>
        <div className="space-y-4">
          <div id="project-default-workflow" tabIndex={-1}>
            <DefaultWorkflowControl />
          </div>
          {allProfiles.map((p) => (
            <ProfileCard
              key={p.id}
              profile={p}
              onClick={() => setSelectedId(p.id)}
              isDisabled={includesWorkflowProfileId(disabledIds, p.id)}
              isLastEnabled={p.isBuiltIn === true && enabledCount <= 1 && !includesWorkflowProfileId(disabledIds, p.id)}
              toggleDisabled={disableMutation.isPending || enableMutation.isPending}
              onToggleDisabled={handleToggleDisabled}
              profileHook={profileHook}
            />
          ))}
        </div>
      </SettingsSection>
    </div>
  )
}
