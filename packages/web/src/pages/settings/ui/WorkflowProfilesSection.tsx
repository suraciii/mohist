import { useLayoutEffect, useRef, useState } from 'react'
import { ArrowLeftIcon } from 'lucide-react'
import { useWorkflowProfiles, useWorkflowProfile } from '../../../entities/settings'
import type { WorkflowProfileInfo } from '../../../entities/settings'
import { CardSection } from '../../../shared/ui/components/card-section'
import { SectionState } from './SectionState'
import { SettingsSection } from './SettingsSection'

// Isolated until workflow profile list data can provide per-profile stages.
export const DEFAULT_WORKFLOW_STAGES = ['plan', 'build', 'check', 'integrate'] as const

function YamlViewer({ yaml }: { yaml: string }) {
  return (
    <pre className="text-xs font-mono leading-relaxed text-foreground whitespace-pre-wrap break-all max-h-[600px] overflow-auto">
      {yaml}
    </pre>
  )
}

function StageSummary({ stage }: { stage: { stage: string; requiresApproval: boolean; tasks: string[]; checks: string[] } }) {
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

function ProfileDetail({ profileId, onBack }: { profileId: string; onBack: () => void }) {
  const { data: profile, isLoading, isError } = useWorkflowProfile(profileId)

  if (isLoading) {
    return (
      <div className="space-y-4">
        <div className="h-4 w-32 bg-muted rounded animate-pulse" />
        <div className="h-40 bg-muted rounded-md animate-pulse" />
      </div>
    )
  }

  if (isError || !profile) {
    return (
      <div className="text-sm text-red-600">Failed to load profile.</div>
    )
  }

  return (
    <SettingsSection title="Workflow Profiles">
      <div>
        <button
          onClick={onBack}
          data-testid="workflow-profile-back"
          className="text-xs text-muted-foreground hover:text-foreground transition-colors mb-2 inline-flex items-center gap-1"
        >
          <ArrowLeftIcon className="w-3 h-3" />
          All profiles
        </button>
        <div className="flex items-center gap-2">
          <h4 className="text-sm font-medium text-foreground">{profile.displayName}</h4>
          {profile.isDefault && (
            <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-green-50 text-green-700 border border-green-200">
              Default
            </span>
          )}
        </div>
        <p className="text-[10px] text-muted-foreground/70 mt-1 font-mono">{profile.id}</p>
        <p
          data-testid="workflow-profile-description"
          className="text-sm text-foreground mt-3 leading-relaxed whitespace-pre-line"
        >
          {profile.description}
        </p>
      </div>

      <CardSection title="Stages" titleAs="h4" className="py-1">
        <div>
          {profile.stages.map((s) => (
            <StageSummary key={s.stage} stage={s} />
          ))}
        </div>
      </CardSection>

      <CardSection title="Shared Stage Definition (YAML)" titleAs="h4">
        <p className="text-[11px] text-muted-foreground mb-3">
          quick-fix and experiment reuse these stages from mohist/default; only the metadata above differs.
        </p>
        <YamlViewer yaml={profile.yaml} />
      </CardSection>
    </SettingsSection>
  )
}

function ProfileCard({ profile, onClick }: { profile: WorkflowProfileInfo; onClick: () => void }) {
  const descriptionRef = useRef<HTMLParagraphElement>(null)
  const [isExpanded, setIsExpanded] = useState(false)
  const [hasOverflow, setHasOverflow] = useState(false)

  useLayoutEffect(() => {
    const description = descriptionRef.current
    if (!description) return

    setHasOverflow(description.scrollHeight > description.clientHeight)
  }, [profile.description])

  return (
    <CardSection className="p-0 hover:bg-muted/50 transition-colors">
      <div data-testid={`workflow-profile-${profile.id}`} className="p-4">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium text-foreground">{profile.displayName}</span>
              {profile.isDefault && (
                <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-green-50 text-green-700 border border-green-200">
                  Default
                </span>
              )}
            </div>
            <p className="text-[10px] text-muted-foreground/70 mt-1 font-mono">{profile.id}</p>
          </div>
          <button
            type="button"
            onClick={onClick}
            className="shrink-0 text-xs font-medium text-primary hover:text-primary/80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 rounded-sm"
          >
            View details
          </button>
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
          {DEFAULT_WORKFLOW_STAGES.map((stage) => (
            <span
              key={stage}
              className="inline-flex items-center rounded-full border bg-muted/50 px-2 py-0.5 text-[10px] font-medium text-muted-foreground"
            >
              {stage}
            </span>
          ))}
        </div>
      </div>
    </CardSection>
  )
}

export function WorkflowProfilesSection() {
  const { data: profiles, isLoading, isError } = useWorkflowProfiles()
  const [selectedId, setSelectedId] = useState<string | null>(null)

  if (selectedId) {
    return <ProfileDetail profileId={selectedId} onBack={() => setSelectedId(null)} />
  }

  if (isLoading) {
    return <SectionState variant="loading" title="Workflow Profiles" skeletonRows={2} />
  }

  if (isError || !profiles) {
    return (
      <SectionState
        variant="error"
        title="Workflow Profiles"
        message="Failed to load profiles."
      />
    )
  }

  return (
    <SettingsSection
      title="Workflow Profiles"
      description="Workflow profiles define how issues move through stages. Click a profile to view its definition."
    >
      <div className="space-y-2">
        {profiles.map((p) => (
          <ProfileCard key={p.id} profile={p} onClick={() => setSelectedId(p.id)} />
        ))}
      </div>
    </SettingsSection>
  )
}
