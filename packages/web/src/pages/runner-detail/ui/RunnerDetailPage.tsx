import { ArrowLeftIcon } from 'lucide-react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { ApiError } from '../../../shared/api/client'
import type { Project } from '../../../entities/project'
import { projectPath, useProject } from '../../../entities/project'
import type { RunnerActiveWork, RunnerRuntime, RunnerStatusEntry } from '../../../entities/runner'
import { useRunner } from '../../../entities/runner'
import { CardSection } from '@/shared/ui/components/card-section'
import { Card } from '@/shared/ui/components/card'
import { Button } from '@/shared/ui/components/button'
import { Badge } from '@/shared/ui/components/badge'
import { RunnerNextActions, SlotsEditor, type SlotsEditorMutationHook } from '../../../widgets/runner-status'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

function displayValue(value: string | number | null | undefined) {
  return value == null || value === '' ? 'unknown' : String(value)
}

function formatTimestamp(value: string | null | undefined): string {
  if (!value) return 'unknown'
  const ms = new Date(value).getTime()
  return Number.isFinite(ms) ? new Date(ms).toLocaleString() : value
}

function reasonLabel(code: string) {
  return code.replaceAll('-', ' ')
}

function Fact({ label, children, testId }: { label: string; children: React.ReactNode; testId?: string }) {
  return (
    <div className="flex min-w-0 flex-wrap items-baseline justify-between gap-x-4 gap-y-1 border-b border-border/50 py-2 last:border-0">
      <dt className="shrink-0 text-xs text-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-words text-right text-sm text-foreground" data-testid={testId}>
        {children}
      </dd>
    </div>
  )
}

function AdmissionReasons({ codes }: { codes: string[] }) {
  if (codes.length === 0) return <span className="text-muted-foreground">none</span>
  return (
    <div className="flex max-w-full flex-wrap justify-end gap-1" data-testid="runner-detail-admission-reasons">
      {codes.map((code) => (
        <span key={code} className="rounded bg-warning-subtle px-1.5 py-0.5 text-[11px] text-warning">
          {reasonLabel(code)}
        </span>
      ))}
    </div>
  )
}

function OwnerKind({ work }: { work: RunnerActiveWork }) {
  const isAgentJob = work.ownerKind.toLowerCase() === 'agent-job' || work.ownerKind.toLowerCase() === 'agentjob'
  return (
    <Badge
      variant={isAgentJob ? 'secondary' : 'outline'}
      className={
        isAgentJob
          ? 'border-info-border bg-info-subtle text-info'
          : 'border-warning-border bg-warning-subtle text-warning'
      }
    >
      {isAgentJob ? 'AgentJob' : 'Workflow'}
    </Badge>
  )
}

function IssueReference({ work, projects }: { work: RunnerActiveWork; projects: Project[] }) {
  if (!work.issue) return null
  const project = projects.find((candidate) => candidate.id === work.issue?.projectId)
  if (!project)
    return (
      <span className="text-xs text-muted-foreground">
        {work.issue.projectId} · #{work.issue.issueNumber}
      </span>
    )
  return (
    <Link
      to={projectPath(project.name, `/issues/${work.issue.issueNumber}`)}
      className="text-xs text-info hover:text-info-foreground hover:underline"
      data-testid="active-work-issue-link"
      data-issue-project-id={work.issue.projectId}
    >
      {project.name} · issue #{work.issue.issueNumber}
    </Link>
  )
}

function ActiveWorkRow({ work, projects }: { work: RunnerActiveWork; projects: Project[] }) {
  return (
    <div
      className="min-w-0 rounded-md border border-border/60 p-3"
      data-testid="active-work-detail-row"
      data-work-id={work.workId}
      data-owner-kind={work.ownerKind}
    >
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <OwnerKind work={work} />
        <span className="min-w-0 break-words text-sm font-medium text-foreground">{work.title ?? work.workType}</span>
        {work.stage && <span className="text-xs text-muted-foreground">stage: {work.stage}</span>}
      </div>
      <div className="mt-2 flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
        <span className="break-all font-mono" data-testid="active-work-work-id">
          {work.workId}
        </span>
        <span>owner</span>
        <span className="break-all font-mono" data-testid="active-work-owner-id">
          {work.ownerId}
        </span>
        <IssueReference work={work} projects={projects} />
      </div>
    </div>
  )
}

function RuntimeCatalog({ runtime }: { runtime: RunnerRuntime }) {
  const catalog = runtime.catalog
  return (
    <div
      className="mt-3 space-y-3 rounded-md border border-border/70 bg-muted/20 p-3"
      data-testid={`runner-runtime-${runtime.name}`}
    >
      <div className="flex min-w-0 flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="break-all text-sm font-semibold text-foreground">{runtime.name}</h3>
          <p className="text-xs text-muted-foreground">
            Runtime readiness and capability catalog are independent facts.
          </p>
        </div>
        <Badge
          variant={runtime.readiness.state === 'ready' ? 'secondary' : 'outline'}
          data-testid="runner-runtime-readiness"
        >
          {runtime.readiness.state}
        </Badge>
      </div>
      <dl className="grid min-w-0 gap-x-4 gap-y-2 sm:grid-cols-2">
        <Fact label="Readiness generation">{displayValue(runtime.readiness.generation)}</Fact>
        <Fact label="Readiness reason">
          {runtime.readiness.reasonCode ? reasonLabel(runtime.readiness.reasonCode) : 'none'}
        </Fact>
        <Fact label="Catalog complete">{catalog?.complete == null ? 'unknown' : catalog.complete ? 'yes' : 'no'}</Fact>
        <Fact label="Capability revision">{displayValue(catalog?.capabilityRevision)}</Fact>
      </dl>
      {catalog ? (
        <div className="grid min-w-0 gap-4 lg:grid-cols-2">
          <div className="min-w-0">
            <h4 className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">
              Models ({catalog.modelCount})
            </h4>
            <div className="mt-1 flex min-w-0 flex-wrap gap-1" data-testid="runner-runtime-models">
              {catalog.models.length === 0 ? (
                <span className="text-xs text-muted-foreground">none</span>
              ) : (
                catalog.models.map((model) => (
                  <code key={model} className="max-w-full break-all rounded bg-muted px-1.5 py-0.5 text-[11px]">
                    {model}
                  </code>
                ))
              )}
            </div>
          </div>
          <div className="min-w-0">
            <h4 className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Variants</h4>
            <div className="mt-1 space-y-1" data-testid="runner-runtime-variants">
              {Object.entries(catalog.variants).length === 0 ? (
                <span className="text-xs text-muted-foreground">none</span>
              ) : (
                Object.entries(catalog.variants).map(([model, variants]) => (
                  <div key={model} className="break-words text-xs">
                    <span className="font-mono">{model}</span>: {variants.join(', ') || 'none'}
                  </div>
                ))
              )}
            </div>
          </div>
          <div className="min-w-0 lg:col-span-2">
            <h4 className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">Reasoning support</h4>
            <div className="mt-1 text-xs text-foreground" data-testid="runner-runtime-reasoning-support">
              {catalog.supportsReasoningEffort == null
                ? 'unknown'
                : catalog.supportsReasoningEffort
                  ? 'supported'
                  : 'not supported'}
            </div>
            {Object.entries(catalog.reasoningEfforts).length > 0 && (
              <div
                className="mt-1 space-y-1 text-xs text-muted-foreground"
                data-testid="runner-runtime-reasoning-efforts"
              >
                {Object.entries(catalog.reasoningEfforts).map(([model, efforts]) => (
                  <div key={model} className="break-words">
                    <span className="font-mono">{model}</span>: {efforts.join(', ') || 'none'}
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      ) : (
        <p className="text-xs text-muted-foreground">No catalog reported. This does not change readiness.</p>
      )}
    </div>
  )
}

function environmentPhaseVariant(phase: string) {
  if (phase === 'active') return 'secondary' as const
  if (phase === 'failed' || phase === 'unconfirmed') return 'destructive' as const
  return 'outline' as const
}

function toolCheckVariant(outcome: string) {
  if (outcome === 'passed') return 'secondary' as const
  if (outcome === 'failed' || outcome === 'timed-out' || outcome === 'error') return 'destructive' as const
  return 'outline' as const
}

function EnvironmentSummary({ row }: { row: RunnerStatusEntry }) {
  const environment = row.environment
  const application = environment?.application
  const observation = environment?.observation

  return (
    <CardSection title="Execution environment" data-testid="runner-detail-environment-section">
      {environment ? (
        <div className="space-y-3">
          <dl data-testid="runner-detail-environment-active">
            <Fact label="Active version" testId="runner-environment-active-version">
              {displayValue(environment.activeVersion)}
            </Fact>
            <Fact label="Loaded at" testId="runner-environment-loaded-at">
              {formatTimestamp(environment.activeLoadedAt)}
            </Fact>
          </dl>

          {application ? (
            <div className="border-t border-border/60 pt-3" data-testid="runner-environment-application">
              <div className="mb-2 flex min-w-0 flex-wrap items-center justify-between gap-2">
                <span className="text-xs font-medium text-foreground">Application</span>
                <Badge variant={environmentPhaseVariant(application.phase)}>{application.phase}</Badge>
              </div>
              <dl>
                <Fact label="Target version">{application.targetVersion}</Fact>
                <Fact label="Previous version">{displayValue(application.previousVersion)}</Fact>
                <Fact label="Update id">
                  <code className="break-all text-xs">{application.updateId}</code>
                </Fact>
                <Fact label="Requested at">{formatTimestamp(application.requestedAt)}</Fact>
                <Fact label="Completed at">{formatTimestamp(application.completedAt)}</Fact>
                <Fact label="Failure">{application.failureCode ? reasonLabel(application.failureCode) : 'none'}</Fact>
                <Fact label="Base process generation">
                  <code className="break-all text-xs">{displayValue(application.baseProcessGeneration)}</code>
                </Fact>
                <Fact label="Base control generation">
                  <code className="break-all text-xs">{displayValue(application.baseConnectionGeneration)}</code>
                </Fact>
              </dl>
            </div>
          ) : (
            <p className="text-xs text-muted-foreground">No environment application recorded.</p>
          )}

          {observation ? (
            <div className="border-t border-border/60 pt-3" data-testid="runner-environment-observation">
              <div className="mb-2 flex min-w-0 flex-wrap items-center justify-between gap-2">
                <span className="text-xs font-medium text-foreground">Latest observation</span>
                <span className="text-xs text-muted-foreground">{formatTimestamp(observation.reportedAt)}</span>
              </div>
              <dl>
                <Fact label="Source">{displayValue(observation.candidateSource)}</Fact>
                <Fact label="User">{displayValue(observation.candidateUser)}</Fact>
                <Fact label="Candidate version">{displayValue(observation.candidateVersion)}</Fact>
                <Fact label="Candidate variables">
                  {observation.candidateVariables.length > 0 ? observation.candidateVariables.join(', ') : 'none'}
                </Fact>
                <Fact label="Added variables">
                  {observation.candidateAddedVariables.length > 0
                    ? observation.candidateAddedVariables.join(', ')
                    : 'none'}
                </Fact>
                <Fact label="Removed variables">
                  {observation.candidateRemovedVariables.length > 0
                    ? observation.candidateRemovedVariables.join(', ')
                    : 'none'}
                </Fact>
                <Fact label="Changed variables">
                  {observation.candidateChangedVariables.length > 0
                    ? observation.candidateChangedVariables.join(', ')
                    : 'none'}
                </Fact>
              </dl>
              <div className="mt-3 border-t border-border/50 pt-3" data-testid="runner-environment-tool-checks">
                <div className="mb-2 text-xs font-medium text-foreground">Tool checks</div>
                {observation.toolChecks.length > 0 ? (
                  <div className="space-y-2">
                    {observation.toolChecks.map((check, index) => (
                      <div
                        key={`${check.executable}-${check.checkedAt}-${index}`}
                        className="flex min-w-0 flex-wrap items-center justify-between gap-2 rounded border border-border/60 px-2 py-2"
                        data-testid="runner-environment-tool-check"
                      >
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <code className="break-all text-xs text-foreground">{check.executable}</code>
                            <Badge variant={toolCheckVariant(check.outcome)}>{check.outcome}</Badge>
                          </div>
                          <div className="mt-1 break-all text-xs text-muted-foreground">
                            {displayValue(check.resolvedPath)} · {check.snapshotKind} · {check.durationMilliseconds}ms
                          </div>
                        </div>
                        <span className="text-xs text-muted-foreground">{formatTimestamp(check.checkedAt)}</span>
                      </div>
                    ))}
                  </div>
                ) : (
                  <p className="text-xs text-muted-foreground">No tool checks reported.</p>
                )}
              </div>
            </div>
          ) : null}

          <div className="border-t border-border/60 pt-3 text-xs text-muted-foreground">
            Capture and apply on the Runner host with{' '}
            <code className="break-all rounded bg-muted px-1.5 py-0.5 text-foreground">
              mo runner environment status --runner-id &lt;runner-id&gt;
            </code>
          </div>
        </div>
      ) : (
        <p className="text-sm text-muted-foreground">No environment identity reported.</p>
      )}
    </CardSection>
  )
}

function RunnerDetailContent({
  row,
  slotsMutationHook,
  projects,
}: {
  row: RunnerStatusEntry
  slotsMutationHook?: SlotsEditorMutationHook
  projects: Project[]
}) {
  const activeWorks = row.activeWorks ?? []
  const capacity = row.capacity
  return (
    <>
      <div className="mb-6" data-testid="runner-detail-header">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <span
            className="max-w-full break-all font-mono text-sm font-medium text-foreground"
            data-testid="runner-detail-id"
          >
            {row.identity.id}
          </span>
          <Badge variant={row.presence.state === 'online' ? 'secondary' : 'outline'}>{row.presence.state}</Badge>
          <Badge variant="outline">control {row.control.state}</Badge>
          <Badge variant={row.admission.state === 'ready' ? 'secondary' : 'destructive'}>
            admission {row.admission.state}
          </Badge>
        </div>
        <h1 className="mt-2 break-all text-2xl font-bold text-foreground">{row.identity.id}</h1>
      </div>

      <div className="grid min-w-0 gap-6 lg:grid-cols-2">
        <CardSection title="Identity">
          <dl data-testid="runner-detail-identity">
            <Fact label="Runner id" testId="runner-detail-id-cell">
              {row.identity.id}
            </Fact>
            <Fact label="Hostname" testId="runner-detail-hostname">
              {displayValue(row.identity.hostname)}
            </Fact>
            <Fact label="Kind" testId="runner-detail-kind">
              {displayValue(row.identity.kind)}
            </Fact>
            <Fact label="Component" testId="runner-detail-component">
              {displayValue(row.identity.component)}
            </Fact>
            <Fact label="Source revision" testId="runner-detail-source-revision">
              {displayValue(row.identity.sourceRevision)}
            </Fact>
            <Fact label="Release" testId="runner-detail-release-id">
              {displayValue(row.identity.releaseId)}
            </Fact>
            <Fact label="Generation" testId="runner-detail-generation">
              {displayValue(row.identity.generation)}
            </Fact>
          </dl>
        </CardSection>

        <CardSection title="Presence, control, and admission">
          <dl data-testid="runner-detail-status-facts">
            <Fact label="Presence">{row.presence.state}</Fact>
            <Fact label="Last observed">{formatTimestamp(row.presence.lastObservedAt)}</Fact>
            <Fact label="Control">{row.control.state}</Fact>
            <Fact label="Control generation">{displayValue(row.control.generation)}</Fact>
            <Fact label="Admission">{row.admission.state}</Fact>
            <Fact label="Admission reasons">
              <AdmissionReasons codes={row.admission.reasonCodes} />
            </Fact>
          </dl>
        </CardSection>

        <CardSection title="Capabilities and configured capacity">
          <div className="space-y-3" data-testid="runner-detail-capabilities">
            <div>
              <div className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Capabilities</div>
              {row.capabilities.length === 0 ? (
                <div className="mt-1 text-xs text-muted-foreground">none</div>
              ) : (
                <div className="mt-1 flex flex-wrap gap-1" data-testid="runner-detail-capability-list">
                  {row.capabilities.map((capability) => (
                    <span
                      key={capability}
                      className="break-all rounded bg-muted px-1.5 py-0.5 text-xs text-muted-foreground"
                    >
                      {capability}
                    </span>
                  ))}
                </div>
              )}
            </div>
            <div className="flex min-w-0 flex-wrap items-center justify-between gap-3 text-sm">
              <span className="text-muted-foreground">Configured slots</span>
              <span data-testid="runner-detail-max-slots">
                {capacity ? (
                  <SlotsEditor runnerId={row.identity.id} value={capacity.total} mutationHook={slotsMutationHook} />
                ) : (
                  'unknown'
                )}
              </span>
            </div>
            <p className="text-[11px] text-muted-foreground">Capacity combines active Workflow and AgentJob owners.</p>
          </div>
        </CardSection>

        <CardSection title="Capacity and drain">
          <dl data-testid="runner-detail-capacity-section">
            <Fact label="Used / total" testId="runner-detail-capacity">
              {capacity ? `${capacity.used == null ? 'unknown' : capacity.used}/${capacity.total} slots` : 'unknown'}
            </Fact>
            <Fact label="Drain">
              {row.drain?.active
                ? `${row.drain.kind}${row.drain.updateInterruptId ? ` · ${row.drain.updateInterruptId}` : ''}`
                : 'not draining'}
            </Fact>
          </dl>
          <div className="mt-3">
            <RunnerNextActions actions={row.nextActions} />
          </div>
        </CardSection>

        <EnvironmentSummary row={row} />

        <CardSection title="Runtimes" data-testid="runner-detail-runtimes-section">
          {row.runtimes.length === 0 ? (
            <p className="text-sm text-muted-foreground">No Runtime witnesses or catalogs reported.</p>
          ) : (
            row.runtimes.map((runtime) => <RuntimeCatalog key={runtime.name} runtime={runtime} />)
          )}
        </CardSection>

        <CardSection title="Active owners" data-testid="runner-detail-active-works-section">
          {activeWorks.length === 0 ? (
            <div className="text-sm text-muted-foreground" data-testid="runner-detail-no-active-works">
              No active owners reported.
            </div>
          ) : (
            <div className="space-y-2" data-testid="runner-detail-active-works-list" data-count={activeWorks.length}>
              {activeWorks.map((work) => (
                <ActiveWorkRow key={work.workId} work={work} projects={projects} />
              ))}
            </div>
          )}
        </CardSection>
      </div>
    </>
  )
}

export interface RunnerDetailPageDependencies {
  runnerHook?: typeof useRunner
  slotsMutationHook?: SlotsEditorMutationHook
}

export function RunnerDetailPage({ dependencies }: { dependencies?: RunnerDetailPageDependencies } = {}) {
  const { runnerId } = useParams<{ runnerId: string }>()
  const navigate = useNavigate()
  const { projects } = useProject()
  const runnerHook = dependencies?.runnerHook ?? useRunner
  const { data: runner, isLoading, error } = runnerHook(runnerId)
  useDocumentTitle(`Runner ${runnerId ?? ''} — Mohist`)

  const backToList = () => navigate('/runners')
  const isNotFound =
    error && (error instanceof ApiError ? error.status === 404 : (error as { status?: number }).status === 404)

  if (isNotFound) {
    return (
      <div className="flex-1 min-w-0 overflow-y-auto" data-testid="runner-detail-page">
        <div className="mx-auto max-w-3xl px-4 py-10 sm:px-6">
          <Card className="p-8 text-center" data-testid="runner-not-found">
            <div className="mb-2 text-lg font-medium text-foreground">Runner not found</div>
            <div className="mb-4 text-sm text-muted-foreground">{`Runner '${runnerId}' is not in the global Runner inventory.`}</div>
            <Button type="button" variant="outline" onClick={backToList} data-testid="runner-not-found-back">
              Back to Runners
            </Button>
          </Card>
        </div>
      </div>
    )
  }
  if (error) {
    return (
      <div className="flex-1 min-w-0 overflow-y-auto" data-testid="runner-detail-page">
        <div className="mx-auto max-w-3xl px-4 py-10 sm:px-6">
          <Card className="p-8 text-center" data-testid="runner-detail-error">
            <div className="mb-2 text-lg font-medium text-foreground">Failed to load Runner</div>
            <div className="mb-4 break-words text-sm text-muted-foreground">{error.message}</div>
            <Button type="button" variant="outline" onClick={backToList}>
              Back to Runners
            </Button>
          </Card>
        </div>
      </div>
    )
  }
  if (isLoading || !runner)
    return (
      <div className="flex flex-1 items-center justify-center" data-testid="runner-detail-loading">
        <div className="text-sm text-muted-foreground">Loading…</div>
      </div>
    )

  return (
    <div className="flex-1 min-w-0 overflow-y-auto" data-testid="runner-detail-page">
      <div className="mx-auto max-w-6xl px-4 py-6 sm:px-6">
        <button
          type="button"
          onClick={backToList}
          className="mb-4 inline-flex items-center gap-1 text-sm text-muted-foreground transition-colors hover:text-foreground"
          data-testid="runner-detail-back"
        >
          <ArrowLeftIcon className="size-3.5" />
          <span>Back to Runners</span>
        </button>
        <RunnerDetailContent row={runner} slotsMutationHook={dependencies?.slotsMutationHook} projects={projects} />
      </div>
    </div>
  )
}
