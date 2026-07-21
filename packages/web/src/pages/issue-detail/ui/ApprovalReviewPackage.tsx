import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { MoreHorizontalIcon } from 'lucide-react'
import {
  useIssueWorkflowArtifactContent,
  useIssueWorkflowArtifacts,
  type IssueDiffResponse,
  type WorkflowArtifact,
  type WorkflowArtifactDirectory,
} from '../../../entities/issue'
import { ArtifactTextContent } from '../../../widgets/issue-workflow'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import type { IssueDecisionAction } from '../model/issueDecisionActions'
import type { IssueDecisionActionController } from '../model/useIssueDecisionActions'
import { IssueDecisionSurface } from './IssueDecisionSurface'
import { useApprovalKeyboardShortcuts } from './useApprovalKeyboardShortcuts'

type ArtifactListHook = (...args: Parameters<typeof useIssueWorkflowArtifacts>) => Pick<ReturnType<typeof useIssueWorkflowArtifacts>, 'data' | 'isLoading' | 'error'>
type ArtifactContentHook = (...args: Parameters<typeof useIssueWorkflowArtifactContent>) => Pick<ReturnType<typeof useIssueWorkflowArtifactContent>, 'data' | 'isLoading' | 'error'>

export type SendBackCategory = 'direction' | 'scope' | 'detail'

export interface SendBackDraft {
  category: SendBackCategory | null
  body: string
}

export function serializeSendBackFeedback(draft: SendBackDraft): string {
  const category = draft.category ? draft.category[0].toUpperCase() + draft.category.slice(1) : ''
  return `Category: ${category}\n\n${draft.body.trim()}`
}

const CATEGORY_LABELS: Array<{ value: SendBackCategory; label: string }> = [
  { value: 'direction', label: 'Direction' },
  { value: 'scope', label: 'Scope' },
  { value: 'detail', label: 'Detail' },
]

interface SendBackFeedbackFormProps {
  draft: SendBackDraft
  onChange: (draft: SendBackDraft) => void
  onCancel: () => void
  onSubmit: () => void
  pending: boolean
  stage: string | null
}

export function SendBackFeedbackForm({
  draft,
  onChange,
  onCancel,
  onSubmit,
  pending,
  stage,
}: SendBackFeedbackFormProps) {
  const textRef = useRef<HTMLTextAreaElement>(null)
  const valid = !!stage && !!draft.category && draft.body.trim().length > 0

  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key !== 'Enter' || !event.metaKey || event.repeat || event.nativeEvent.isComposing) return
    event.preventDefault()
    if (valid && !pending) onSubmit()
  }

  useEffect(() => {
    textRef.current?.focus()
  }, [])

  return (
    <div data-testid="send-back-feedback-form" className="mt-3 min-w-0 rounded-md border border-border bg-muted p-3">
      <div className="space-y-2">
        <div className="text-xs font-medium text-card-foreground">What needs to change?</div>
        <div role="radiogroup" aria-label="Feedback category" className="grid grid-cols-3 gap-2">
          {CATEGORY_LABELS.map((category) => (
            <button
              key={category.value}
              type="button"
              role="radio"
              aria-checked={draft.category === category.value}
              onClick={() => onChange({ ...draft, category: category.value })}
              className={cn(
                'min-h-9 rounded-md border px-2 text-xs font-medium',
                draft.category === category.value
                  ? 'border-primary bg-primary text-primary-foreground'
                  : 'border-border bg-card text-card-foreground hover:bg-background',
              )}
            >
              {category.label}
            </button>
          ))}
        </div>
        <Textarea
          ref={textRef}
          id="send-back-feedback-body"
          data-testid="send-back-feedback-textarea"
          value={draft.body}
          onChange={(event) => onChange({ ...draft, body: event.target.value })}
          onKeyDown={handleKeyDown}
          rows={4}
          className="mt-2 min-w-0 resize-y bg-card"
          placeholder="Describe the requested change..."
          aria-required="true"
        />
        <div className="text-xs text-muted-foreground">Submit with <kbd className="rounded border border-border bg-card px-1 py-0.5 font-mono">Command+Enter</kbd></div>
      </div>
      <div className="mt-2 flex justify-end gap-2">
        <Button type="button" variant="ghost" size="sm" onClick={onCancel} disabled={pending}>
          Cancel
        </Button>
        <Button type="button" size="sm" data-testid="send-back-feedback-submit" disabled={!valid || pending} onClick={onSubmit}>
          {pending ? 'Sending back...' : 'Submit feedback'}
        </Button>
      </div>
    </div>
  )
}

type ArtifactValue = WorkflowArtifact | WorkflowArtifactDirectory

function artifactError(error: unknown): string {
  return error instanceof Error ? error.message : 'Failed to load artifact content.'
}

function ArtifactEvidence({
  issueNumber,
  artifact,
  contentHook = useIssueWorkflowArtifactContent,
}: {
  issueNumber: number
  artifact: ArtifactValue | null
  contentHook?: ArtifactContentHook
}) {
  const content = contentHook(issueNumber, artifact?.artifactId ?? null, {}, !!artifact)

  if (!artifact) return <div className="text-xs text-muted-foreground">Artifact is missing from this workflow run.</div>
  if (artifact.kind === 'directory') return <div className="text-xs text-warning">Unexpected directory artifact; expected a file.</div>
  if (content.isLoading) return <div className="text-xs text-muted-foreground">Loading {artifact.path}...</div>
  if (content.error) return <div className="text-xs text-danger">{artifactError(content.error)}</div>
  if (!content.data) return <div className="text-xs text-muted-foreground">No recorded content for {artifact.path}.</div>
  if (content.data.kind === 'directory') return <div className="text-xs text-warning">Unexpected directory content; expected a file.</div>

  return <ArtifactTextContent content={content.data.content} contentType={content.data.contentType} />
}

function ArtifactSlot({
  issueNumber,
  path,
  workflowRunId,
  artifactsHook = useIssueWorkflowArtifacts,
  contentHook,
}: {
  issueNumber: number
  path: string
  workflowRunId: string | null
  artifactsHook?: ArtifactListHook
  contentHook?: ArtifactContentHook
}) {
  const query = artifactsHook(issueNumber, { path }, !!workflowRunId, workflowRunId)
  const artifact = query.data?.find((item) => item.path === path) ?? null

  return (
    <section data-testid={`approval-artifact-${path}`} className="min-w-0 space-y-2">
      <h3 className="text-sm font-semibold text-card-foreground">{path}</h3>
      {query.isLoading && <div className="text-xs text-muted-foreground">Loading artifact list...</div>}
      {query.error && <div className="text-xs text-danger">Failed to load artifact list.</div>}
      {!query.isLoading && !query.error && <ArtifactEvidence issueNumber={issueNumber} artifact={artifact} contentHook={contentHook} />}
    </section>
  )
}

function DiffSummary({ data, isLoading, error }: { data?: IssueDiffResponse; isLoading: boolean; error: unknown }) {
  if (isLoading) return <div data-testid="approval-diff-summary" className="text-xs text-muted-foreground">Loading diff summary...</div>
  if (error) return <div data-testid="approval-diff-summary" className="text-xs text-danger">Failed to load diff summary.</div>
  if (!data || data.available === false) {
    return <div data-testid="approval-diff-summary" className="text-xs text-muted-foreground">Diff summary is unavailable: {data?.message ?? 'No comparison is available.'}</div>
  }

  return (
    <section data-testid="approval-diff-summary" className="min-w-0 space-y-2 rounded-md border border-border bg-muted/50 p-3 text-sm">
      <h3 className="font-semibold text-card-foreground">Current diff</h3>
      <div className="flex min-w-0 flex-wrap gap-x-3 gap-y-1 text-xs text-muted-foreground">
        <span className="min-w-0 break-all">{data.head} compared with {data.base}</span>
        <span>{data.summary.filesChanged} files changed</span>
        <span className="text-success">+{data.summary.additions}</span>
        <span className="text-danger">-{data.summary.deletions}</span>
      </div>
    </section>
  )
}

function SecondaryActions({
  actions,
  controller,
}: {
  actions: ReadonlyArray<IssueDecisionAction>
  controller: IssueDecisionActionController
}) {
  const [open, setOpen] = useState(false)
  if (actions.length === 0) return null

  return (
    <div className="relative" data-testid="approval-more-actions">
      <Button type="button" variant="outline" size="icon" aria-label="More actions" title="More actions" onClick={() => setOpen((value) => !value)}>
        <MoreHorizontalIcon className="size-4" />
      </Button>
      {open && (
        <div className="absolute bottom-full right-0 mb-2 min-w-52 rounded-md border border-border bg-popover p-1 shadow-lg" role="menu">
          {actions.map((action) => {
            const disabled = !action.enabled || controller.pendingKind !== null
            const reason = controller.pendingKind !== null
              ? 'Another request is in progress. Wait for it to finish before trying again.'
              : action.reason
            if ((action.kind === 'ask-agent' || action.kind === 'view-transcript') && action.to) {
              return (
                <Link
                  key={`${action.kind}-${action.order}`}
                  to={disabled ? '#' : action.to}
                  role="menuitem"
                  aria-disabled={disabled}
                  title={reason ?? undefined}
                  tabIndex={disabled ? -1 : 0}
                  onClick={(event) => {
                    if (disabled) event.preventDefault()
                    else controller.runAction(action)
                  }}
                  className={cn('block rounded px-3 py-2 text-sm', disabled ? 'opacity-50' : 'hover:bg-muted')}
                  data-testid={`approval-more-action-${action.kind}`}
                >
                  {action.label}
                </Link>
              )
            }
            return (
              <button
                key={`${action.kind}-${action.order}`}
                type="button"
                role="menuitem"
                disabled={disabled}
                title={reason ?? undefined}
                onClick={() => {
                  if (action.kind === 'stop') controller.openStopConfirm()
                  else controller.runAction(action)
                  setOpen(false)
                }}
                className="block w-full rounded px-3 py-2 text-left text-sm hover:bg-muted disabled:opacity-50"
                data-testid={`approval-more-action-${action.kind}`}
              >
                {action.label}
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}

export interface ApprovalReviewPackageProps {
  issueNumber: number
  workflowRunId: string | null
  approvalStage: string | null
  actions: ReadonlyArray<IssueDecisionAction>
  controller: IssueDecisionActionController
  rationale: string
  nextAction: string
  isNarrowViewport: boolean
  diffData?: IssueDiffResponse
  diffIsLoading?: boolean
  diffError?: unknown
  artifactListHook?: ArtifactListHook
  artifactContentHook?: ArtifactContentHook
}

export function ApprovalReviewPackage({
  issueNumber,
  workflowRunId,
  approvalStage,
  actions,
  controller,
  rationale,
  nextAction,
  isNarrowViewport,
  diffData,
  diffIsLoading = false,
  diffError,
  artifactListHook,
  artifactContentHook,
}: ApprovalReviewPackageProps) {
  const [sendBackOpen, setSendBackOpen] = useState(false)
  const [draft, setDraft] = useState<SendBackDraft>({ category: null, body: '' })
  const approve = actions.find((action) => action.kind === 'approve') ?? null
  const sendBack = actions.find((action) => action.kind === 'send-back') ?? null
  const secondary = actions.filter((action) => action.kind !== 'approve' && action.kind !== 'send-back')
  const approveReason = controller.pendingKind !== null
    ? 'Another request is in progress. Wait for it to finish before trying again.'
    : approve?.reason
  const sendBackReason = controller.pendingKind !== null
    ? 'Another request is in progress. Wait for it to finish before trying again.'
    : sendBack?.reason
  const handleSendBackOpen = () => setSendBackOpen(true)
  const handleSendBackSubmit = () => {
    if (!sendBack || !sendBack.enabled || controller.pendingKind !== null || !draft.category || !draft.body.trim()) return
    controller.runAction(sendBack, { sendBackBody: serializeSendBackFeedback(draft) })
  }

  useApprovalKeyboardShortcuts({
    actions,
    controller,
    isNarrowViewport,
    onSendBackOpen: handleSendBackOpen,
  })

  const sendBackForm = sendBackOpen && (
    <SendBackFeedbackForm
      draft={draft}
      onChange={setDraft}
      onCancel={() => setSendBackOpen(false)}
      onSubmit={handleSendBackSubmit}
      pending={controller.pendingKind !== null}
      stage={approvalStage}
    />
  )

  const evidence: ReactNode = approvalStage === 'plan' ? (
    <div data-testid="approval-review-evidence" className="mt-4 min-w-0 space-y-5">
      <ArtifactSlot issueNumber={issueNumber} path="proposal.md" workflowRunId={workflowRunId} artifactsHook={artifactListHook} contentHook={artifactContentHook} />
      <ArtifactSlot issueNumber={issueNumber} path="tasks.json" workflowRunId={workflowRunId} artifactsHook={artifactListHook} contentHook={artifactContentHook} />
    </div>
  ) : approvalStage === 'check' ? (
    <div data-testid="approval-review-evidence" className="mt-4 min-w-0 space-y-5">
      <ArtifactSlot issueNumber={issueNumber} path="review.md" workflowRunId={workflowRunId} artifactsHook={artifactListHook} contentHook={artifactContentHook} />
      <DiffSummary data={diffData} isLoading={diffIsLoading} error={diffError} />
    </div>
  ) : (
    <div data-testid="approval-review-evidence" className="mt-4 rounded-md border border-border bg-muted px-3 py-2 text-xs text-muted-foreground">
      No inline evidence is configured for this approval stage.
    </div>
  )

  const actionsForSurface = isNarrowViewport ? [] : actions

  return (
    <section
      data-testid="approval-review-package"
      data-stage={approvalStage ?? 'unknown'}
      className={cn('min-w-0', isNarrowViewport && 'pb-28')}
    >
      {isNarrowViewport ? (
        <div className="min-w-0 rounded-lg border border-l-4 border-warning bg-card p-4 shadow-sm">
          <p className="text-sm text-muted-foreground">{rationale}</p>
          <div className="mt-3 flex flex-wrap items-center gap-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
            <span>Next action</span><span className="normal-case font-normal text-card-foreground">{nextAction}</span>
          </div>
          {evidence}
        </div>
      ) : (
        <IssueDecisionSurface
          actions={actionsForSurface}
          summary="approval-required"
          rationale={rationale}
          nextAction={nextAction}
          controller={controller}
          evidence={evidence}
          sendBackOpen={sendBackOpen}
          onSendBackOpen={handleSendBackOpen}
          sendBackForm={sendBackForm}
          shortcutHints={{
            approve: approve?.enabled && controller.pendingKind === null ? 'a' : undefined,
            'send-back': sendBack?.enabled && controller.pendingKind === null ? 'm' : undefined,
          }}
        />
      )}

      {isNarrowViewport && (
        <div data-testid="approval-mobile-action-bar" className="fixed inset-x-0 bottom-0 z-50 px-3 pb-[calc(0.5rem+env(safe-area-inset-bottom))]">
          <div className="mx-auto grid w-full max-w-md grid-cols-[1fr_1fr_auto] gap-2 rounded-xl border border-border bg-popover/95 p-2 shadow-lg backdrop-blur">
            <Button type="button" data-testid="approval-mobile-approve" aria-describedby={approveReason ? 'approval-mobile-approve-reason' : undefined} disabled={!approve?.enabled || controller.pendingKind !== null} onClick={() => approve && controller.runAction(approve)} className="min-h-11">
              {controller.pendingKind === 'approve' ? 'Approving...' : 'Approve'}
            </Button>
            <Button type="button" variant="destructive" data-testid="approval-mobile-send-back" aria-describedby={sendBackReason ? 'approval-mobile-send-back-reason' : undefined} disabled={!sendBack?.enabled || controller.pendingKind !== null} onClick={handleSendBackOpen} className="min-h-11">
              Send back
            </Button>
            <SecondaryActions actions={secondary} controller={controller} />
          </div>
          {approveReason && <p id="approval-mobile-approve-reason" className="sr-only">{approveReason}</p>}
          {sendBackReason && <p id="approval-mobile-send-back-reason" className="sr-only">{sendBackReason}</p>}
          {sendBackForm}
        </div>
      )}
    </section>
  )
}
