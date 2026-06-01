import { useState } from 'react'
import { ArrowLeftIcon } from 'lucide-react'
import { useWorkflowProfiles, useWorkflowProfile } from '../../../entities/settings'
import type { WorkflowProfileInfo } from '../../../entities/settings'
import { SectionState } from './SectionState'

function YamlViewer({ yaml }: { yaml: string }) {
  return (
    <pre className="text-xs font-mono leading-relaxed text-foreground/85 whitespace-pre-wrap break-all bg-muted/40 rounded-md p-4 border max-h-[600px] overflow-auto">
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
    <div className="space-y-6">
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
          <h3 className="text-sm font-medium text-foreground">{profile.displayName}</h3>
          {profile.isDefault && (
            <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-green-50 text-green-700 border border-green-200">
              Default
            </span>
          )}
        </div>
        <p className="text-xs text-muted-foreground mt-1">{profile.description}</p>
      </div>

      <div className="space-y-3">
        <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Stages</h4>
        <div className="rounded-md border px-4 py-1">
          {profile.stages.map((s) => (
            <StageSummary key={s.stage} stage={s} />
          ))}
        </div>
      </div>

      <div className="space-y-3">
        <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Definition (YAML)</h4>
        <YamlViewer yaml={profile.yaml} />
      </div>
    </div>
  )
}

function ProfileCard({ profile, onClick }: { profile: WorkflowProfileInfo; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      data-testid={`workflow-profile-${profile.id}`}
      className="w-full text-left rounded-md border px-4 py-3 hover:bg-muted/50 transition-colors"
    >
      <div className="flex items-center gap-2">
        <span className="text-sm font-medium text-foreground">{profile.displayName}</span>
        {profile.isDefault && (
          <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-medium bg-green-50 text-green-700 border border-green-200">
            Default
          </span>
        )}
      </div>
      <p className="text-xs text-muted-foreground mt-1 line-clamp-2">{profile.description}</p>
      <p className="text-[11px] text-muted-foreground/70 mt-2 font-mono">{profile.id}</p>
    </button>
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
    <div className="space-y-4">
      <div>
        <h3 className="text-sm font-medium text-foreground">Workflow Profiles</h3>
        <p className="text-xs text-muted-foreground mt-1">
          Workflow profiles define how issues move through stages. Click a profile to view its definition.
        </p>
      </div>
      <div className="space-y-2">
        {profiles.map((p) => (
          <ProfileCard key={p.id} profile={p} onClick={() => setSelectedId(p.id)} />
        ))}
      </div>
    </div>
  )
}
