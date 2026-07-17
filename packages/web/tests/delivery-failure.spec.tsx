import { beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import {
  resolveDeliveryFailureFromMessage,
  resolveDeliveryFailureFromOutput,
  isDeliveryFailureKind,
  isWorkspaceSetupFailureKind,
  getDeliveryFailureGuidance,
  extractBranchInvariantEvidence,
  extractWorkspaceSetupEvidence,
  type DeliveryFailureKind,
  type BranchInvariantEvidence,
  type WorkspaceSetupEvidence,
} from '../src/shared/lib/delivery-failure'
import { WorkflowView } from '../src/widgets/issue-workflow/ui/WorkflowView'
import {
  IssueStatus,
  IssueHealth,
  WorkflowStage,
  type Issue,
  type WorkflowTimeline,
} from '../src/entities/issue'
import { render } from './test-utils'

const EXPECTED_GUIDANCE: Record<DeliveryFailureKind, { label: string; nextAction: string }> = {
  conflict: {
    label: 'Conflict needs attention',
    nextAction: 'Conflicts could not be resolved automatically. Inspect the conflicting files, resolve them on the issue branch, and rerun prepare.',
  },
  'base-moved': {
    label: 'Base branch moved',
    nextAction: 'The base branch moved during publish. Prepare the branch again, then publish. The workflow integrate retry will re-fetch and rebase before re-attempting the merge.',
  },
  'retry-safe': {
    label: 'Transient failure',
    nextAction: 'Retry the task — the failure is unrelated to conflicts or base movement.',
  },
  'branch-invariant-violation': {
    label: 'Runner / action branch-invariant violation',
    nextAction: 'This is a runner or action bug: the workflow workspace left its expected run branch. Retry the task — the runner will restore the run branch automatically — and report the issue if it recurs. Issue work is not the cause.',
  },
  'workspace-setup': {
    label: 'Workflow workspace setup failure',
    nextAction: 'The runner could not prepare the workflow workspace for this run (clone, base branch, or checkout failed). Issue work is not the cause — check the repository gitUrl / base branch and the runner\'s workspace root, then retry.',
  },
  'config-error': {
    label: 'Runner environment is misconfigured',
    nextAction: 'Install the GitHub CLI (`gh`) on the runner host and run `gh auth login` to authenticate with GitHub. Then re-run the issue. The workflow will not auto-retry this kind — environment fixes need a human before the next attempt.',
  },
  'protection-conflict': {
    label: 'Branch protection blocked the merge',
    nextAction: 'GitHub rejected the merge because branch protection requires status checks or reviews that this run cannot satisfy. Adjust the repository\'s branch-protection rules (or switch this issue to the `mohist/local` workflow) and re-run. The workflow will not auto-retry this kind.',
  },
  'pr-state-conflict': {
    label: 'Pull request state changed externally',
    nextAction: 'The pull request was closed or its state changed outside the runner between workflow steps (for example, by a human via the GitHub UI). Decide whether to re-open the PR or abandon it, then re-run or close the issue. The workflow will not auto-retry this kind.',
  },
}

const EMPTY_RESOLUTION = { failureKind: null, guidance: null, evidence: null, workspaceEvidence: null }

describe('delivery-failure guidance mapping', () => {
  it('recognises all delivery failure kinds', () => {
    for (const kind of Object.keys(EXPECTED_GUIDANCE) as DeliveryFailureKind[]) {
      expect(isDeliveryFailureKind(kind)).toBe(true)
      expect(isDeliveryFailureKind(`not-${kind}`)).toBe(false)
      expect(isDeliveryFailureKind(null)).toBe(false)
      expect(isDeliveryFailureKind(undefined)).toBe(false)
      expect(isDeliveryFailureKind(42)).toBe(false)
    }
  })

  it('exposes a guidance record for each delivery failure kind', () => {
    for (const kind of Object.keys(EXPECTED_GUIDANCE) as DeliveryFailureKind[]) {
      const guidance = getDeliveryFailureGuidance(kind)
      expect(guidance.failureKind).toBe(kind)
      expect(guidance.label).toBe(EXPECTED_GUIDANCE[kind].label)
      expect(guidance.nextAction).toBe(EXPECTED_GUIDANCE[kind].nextAction)
    }
  })

  describe('resolveDeliveryFailureFromMessage', () => {
    it('extracts the conflict kind from a prepare failure message', () => {
      const result = resolveDeliveryFailureFromMessage('Prepare failed (conflict): CONFLICT in foo.ts')
      expect(result.failureKind).toBe<DeliveryFailureKind>('conflict')
      expect(result.guidance).toMatchObject({
        failureKind: 'conflict',
        label: EXPECTED_GUIDANCE.conflict.label,
        nextAction: EXPECTED_GUIDANCE.conflict.nextAction,
      })
    })

    it('extracts the base-moved kind from a publish failure message', () => {
      const result = resolveDeliveryFailureFromMessage('Publish failed (base-moved): non-fast-forward')
      expect(result.failureKind).toBe<DeliveryFailureKind>('base-moved')
      expect(result.guidance?.label).toBe(EXPECTED_GUIDANCE['base-moved'].label)
      expect(result.guidance?.nextAction).toBe(EXPECTED_GUIDANCE['base-moved'].nextAction)
    })

    it('extracts the retry-safe kind from a transient failure message', () => {
      const result = resolveDeliveryFailureFromMessage('Publish failed (retry-safe): network reset')
      expect(result.failureKind).toBe<DeliveryFailureKind>('retry-safe')
      expect(result.guidance?.label).toBe(EXPECTED_GUIDANCE['retry-safe'].label)
      expect(result.guidance?.nextAction).toBe(EXPECTED_GUIDANCE['retry-safe'].nextAction)
    })

    it('extracts the branch-invariant-violation kind from a runner failure message', () => {
      const message =
        "branch-invariant violation at start boundary for Prepare branch: expected branch 'mohist/run-wr-1', observed 'master'"
      const result = resolveDeliveryFailureFromMessage(message)
      expect(result.failureKind).toBe<DeliveryFailureKind>('branch-invariant-violation')
      expect(result.guidance?.label).toBe(EXPECTED_GUIDANCE['branch-invariant-violation'].label)
      expect(result.guidance?.nextAction).toBe(EXPECTED_GUIDANCE['branch-invariant-violation'].nextAction)
      expect(result.evidence).toMatchObject<Partial<BranchInvariantEvidence>>({
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: 'master',
        boundary: 'start',
      })
    })

    it('extracts branch evidence for a detached HEAD branch-invariant-violation', () => {
      const message =
        "branch-invariant violation at end boundary for Publish changes: expected branch 'mohist/run-wr-1', observed detached at abc123"
      const result = resolveDeliveryFailureFromMessage(message)
      expect(result.failureKind).toBe<DeliveryFailureKind>('branch-invariant-violation')
      expect(result.evidence).toMatchObject<Partial<BranchInvariantEvidence>>({
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: '',
        observedRef: 'abc123',
        boundary: 'end',
      })
    })

    it('returns null guidance when no kind is present in the message', () => {
      const result = resolveDeliveryFailureFromMessage('Some unrelated failure text')
      expect(result).toEqual(EMPTY_RESOLUTION)
    })

    it('returns null guidance when the message is empty', () => {
      expect(resolveDeliveryFailureFromMessage(null)).toEqual(EMPTY_RESOLUTION)
      expect(resolveDeliveryFailureFromMessage(undefined)).toEqual(EMPTY_RESOLUTION)
      expect(resolveDeliveryFailureFromMessage('')).toEqual(EMPTY_RESOLUTION)
    })

    it('does not confuse a dirty-worktree message with branch-invariant-violation', () => {
      const result = resolveDeliveryFailureFromMessage(
        'Prepare failed (dirty-worktree): staged changes left behind',
      )
      expect(result.failureKind).toBeNull()
      expect(result.guidance).toBeNull()
    })

    it('extracts the workspace-setup kind from a runner failure message', () => {
      const result = resolveDeliveryFailureFromMessage(
        'could not prepare workflow workspace (workspace-setup): git clone failed for ...',
      )
      expect(result.failureKind).toBe<DeliveryFailureKind>('workspace-setup')
      expect(result.guidance?.label).toBe(EXPECTED_GUIDANCE['workspace-setup'].label)
      expect(result.guidance?.nextAction).toBe(EXPECTED_GUIDANCE['workspace-setup'].nextAction)
      expect(result.workspaceEvidence).toBeNull()
    })
  })

  describe('resolveDeliveryFailureFromOutput', () => {
    it('extracts the kind from a parsed prepare JSON output', () => {
      const output = JSON.stringify({
        kind: 'prepare',
        status: 'failed',
        failureKind: 'conflict',
        conflicts: ['a.ts'],
      })
      const result = resolveDeliveryFailureFromOutput(output)
      expect(result.failureKind).toBe<DeliveryFailureKind>('conflict')
      expect(result.guidance?.label).toBe(EXPECTED_GUIDANCE.conflict.label)
    })

    it('extracts the kind from a publish JSON object', () => {
      const result = resolveDeliveryFailureFromOutput({
        kind: 'publish',
        failureKind: 'base-moved',
      })
      expect(result.failureKind).toBe<DeliveryFailureKind>('base-moved')
      expect(result.guidance?.label).toBe(EXPECTED_GUIDANCE['base-moved'].label)
    })

    it('extracts the kind from a nested object with .output field', () => {
      const result = resolveDeliveryFailureFromOutput({
        output: JSON.stringify({ failureKind: 'retry-safe' }),
      })
      expect(result.failureKind).toBe<DeliveryFailureKind>('retry-safe')
    })

    it('extracts the branch-invariant-violation kind from the runner output JSON', () => {
      const result = resolveDeliveryFailureFromOutput({
        kind: 'branch-invariant-violation',
        boundary: 'start',
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: 'master',
      })
      expect(result.failureKind).toBe<DeliveryFailureKind>('branch-invariant-violation')
      expect(result.evidence).toEqual<BranchInvariantEvidence>({
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: 'master',
        observedRef: null,
        boundary: 'start',
      })
    })

    it('extracts the branch-invariant-violation kind from a JSON string output', () => {
      const output = JSON.stringify({
        kind: 'branch-invariant-violation',
        boundary: 'end',
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: '',
        observedRef: 'abc123',
      })
      const result = resolveDeliveryFailureFromOutput(output)
      expect(result.failureKind).toBe<DeliveryFailureKind>('branch-invariant-violation')
      expect(result.evidence).toMatchObject<Partial<BranchInvariantEvidence>>({
        expectedBranch: 'mohist/run-wr-1',
        observedRef: 'abc123',
        boundary: 'end',
      })
    })

    it('extracts the branch-invariant-violation kind nested under branchStability evidence', () => {
      const result = resolveDeliveryFailureFromOutput({
        branchStability: [
          {
            kind: 'branch-stability',
            boundary: 'start',
            expectedBranch: 'mohist/run-wr-1',
            observedBranch: 'mohist/run-wr-1',
          },
          {
            kind: 'branch-invariant-violation',
            boundary: 'end',
            expectedBranch: 'mohist/run-wr-1',
            observedBranch: 'master',
          },
        ],
      })
      expect(result.failureKind).toBe<DeliveryFailureKind>('branch-invariant-violation')
      expect(result.evidence).toMatchObject<Partial<BranchInvariantEvidence>>({
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: 'master',
        boundary: 'end',
      })
    })

    it('falls back to a nested message when no failureKind is present', () => {
      const result = resolveDeliveryFailureFromOutput({ message: 'Prepare failed (conflict): foo' })
      expect(result.failureKind).toBe<DeliveryFailureKind>('conflict')
    })

    it('returns null guidance when neither kind nor message is present', () => {
      expect(resolveDeliveryFailureFromOutput(null)).toEqual(EMPTY_RESOLUTION)
      expect(resolveDeliveryFailureFromOutput({ kind: 'prepare' })).toEqual(EMPTY_RESOLUTION)
    })

    it('ignores an unknown failure kind value', () => {
      const result = resolveDeliveryFailureFromOutput({ failureKind: 'something-else' })
      expect(result).toEqual(EMPTY_RESOLUTION)
    })

    it('extracts workspace-setup from a runner output object', () => {
      const result = resolveDeliveryFailureFromOutput({
        kind: 'workspace-setup',
        workspacePath: '/home/runner/workspaces/proj_x/run_abc',
        status: 'failed',
      })
      expect(result.failureKind).toBe<DeliveryFailureKind>('workspace-setup')
      expect(result.guidance?.label).toBe(EXPECTED_GUIDANCE['workspace-setup'].label)
      expect(result.workspaceEvidence).toEqual<WorkspaceSetupEvidence>({
        workspacePath: '/home/runner/workspaces/proj_x/run_abc',
      })
    })

    it('does not confuse dirty-worktree in output with workspace-setup kind', () => {
      const result = resolveDeliveryFailureFromOutput({
        kind: 'dirty-worktree',
        message: 'staged changes left behind',
      })
      expect(result.failureKind).toBeNull()
    })
  })

  describe('extractBranchInvariantEvidence', () => {
    it('extracts evidence from a runner output object', () => {
      const evidence = extractBranchInvariantEvidence({
        kind: 'branch-invariant-violation',
        boundary: 'start',
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: 'master',
        observedRef: null,
      })
      expect(evidence).toEqual<BranchInvariantEvidence>({
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: 'master',
        observedRef: null,
        boundary: 'start',
      })
    })

    it('extracts evidence from a runner message', () => {
      const evidence = extractBranchInvariantEvidence(
        "branch-invariant violation at end boundary for Publish: expected branch 'mohist/run-wr-1', observed 'master'",
      )
      expect(evidence).toMatchObject<Partial<BranchInvariantEvidence>>({
        expectedBranch: 'mohist/run-wr-1',
        observedBranch: 'master',
        boundary: 'end',
      })
    })

    it('returns null when the source is unrelated', () => {
      expect(extractBranchInvariantEvidence(null)).toBeNull()
      expect(extractBranchInvariantEvidence({ kind: 'prepare' })).toBeNull()
      expect(extractBranchInvariantEvidence('Some other failure')).toBeNull()
    })
  })

  describe('workspace-setup evidence + predicate', () => {
    it('isWorkspaceSetupFailureKind recognises the workspace-setup kind and rejects the others', () => {
      expect(isWorkspaceSetupFailureKind('workspace-setup')).toBe(true)
      expect(isWorkspaceSetupFailureKind('conflict')).toBe(false)
      expect(isWorkspaceSetupFailureKind('base-moved')).toBe(false)
      expect(isWorkspaceSetupFailureKind('retry-safe')).toBe(false)
      expect(isWorkspaceSetupFailureKind('branch-invariant-violation')).toBe(false)
      expect(isWorkspaceSetupFailureKind(null)).toBe(false)
      expect(isWorkspaceSetupFailureKind(undefined)).toBe(false)
      expect(isWorkspaceSetupFailureKind(42)).toBe(false)
    })

    it('extractWorkspaceSetupEvidence pulls workspacePath', () => {
      const evidence = extractWorkspaceSetupEvidence({
        kind: 'workspace-setup',
        workspacePath: '/home/runner/workspaces/proj_x/run_abc',
      })
      expect(evidence).toEqual<WorkspaceSetupEvidence>({
        workspacePath: '/home/runner/workspaces/proj_x/run_abc',
      })
    })

    it('extractWorkspaceSetupEvidence returns null for unrelated sources', () => {
      expect(extractWorkspaceSetupEvidence(null)).toBeNull()
      expect(extractWorkspaceSetupEvidence({ kind: 'prepare' })).toBeNull()
      expect(extractWorkspaceSetupEvidence({ kind: 'workspace-setup' })).toBeNull()
    })

    it('extractWorkspaceSetupEvidence walks into a nested output string', () => {
      const evidence = extractWorkspaceSetupEvidence({
        output: JSON.stringify({
          kind: 'workspace-setup',
          workspacePath: '/srv/workspaces/proj_x/run_abc',
        }),
      })
      expect(evidence).toEqual<WorkspaceSetupEvidence>({
        workspacePath: '/srv/workspaces/proj_x/run_abc',
      })
    })
  })
})

let timelineData: WorkflowTimeline | null = null

beforeEach(() => {
  timelineData = null
})

type FailureKind = Exclude<DeliveryFailureKind, 'branch-invariant-violation'>
const DELIVERY_TASK_KIND: Record<FailureKind, 'prepare' | 'publish' | 'publish-via-pr'> = {
  conflict: 'prepare',
  'base-moved': 'publish',
  'retry-safe': 'prepare',
  'workspace-setup': 'prepare',
  'config-error': 'publish-via-pr',
  'protection-conflict': 'publish-via-pr',
  'pr-state-conflict': 'publish-via-pr',
}

function makeFailureTimeline(kind: FailureKind): WorkflowTimeline {
  const taskKind = DELIVERY_TASK_KIND[kind]
  const taskId = `integrate:${taskKind}.1`
  const title = taskKind === 'publish' || taskKind === 'publish-via-pr'
    ? taskKind === 'publish-via-pr'
      ? 'Publish via PR'
      : 'Publish changes'
    : 'Prepare branch'
  const uses = taskKind === 'publish'
    ? 'mohist/publish'
    : taskKind === 'publish-via-pr'
      ? 'mohist/publish-via-pr'
      : 'mohist/prepare'
  const failureMessage =
    kind === 'conflict'
      ? 'Prepare failed (conflict): CONFLICT in foo.ts'
      : kind === 'base-moved'
        ? 'Publish failed (base-moved): non-fast-forward'
          : kind === 'retry-safe'
            ? 'Prepare failed (retry-safe): network reset'
            : kind === 'workspace-setup'
              ? 'could not prepare workflow workspace (workspace-setup): git clone failed for ...'
              : kind === 'config-error'
                  ? 'Publish via PR failed (config-error): gh CLI not found'
                  : kind === 'protection-conflict'
                    ? 'Publish via PR failed (protection-conflict): required status checks are not satisfied'
                    : 'Publish via PR failed (pr-state-conflict): PR was closed externally'
  const structuredOutput = JSON.stringify({ kind: 'publish-via-pr', failureKind: kind })
  return {
    workflowRunId: 'workflow-run-1',
    status: 'failed',
    currentStage: WorkflowStage.Integrate,
    pendingWork: null,
    stages: [
      {
        stage: WorkflowStage.Integrate,
        status: 'failed',
        order: 4,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: '2026-01-01T00:01:00.000Z',
        durationMs: 60000,
        tasks: [
          {
            id: taskId,
            title,
            uses,
            status: 'failed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:01:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: failureMessage,
            output: structuredOutput,
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

function makeBranchViolationTimeline(): WorkflowTimeline {
  return {
    workflowRunId: 'workflow-run-1',
    status: 'failed',
    currentStage: WorkflowStage.Integrate,
    pendingWork: null,
    stages: [
      {
        stage: WorkflowStage.Integrate,
        status: 'failed',
        order: 4,
        startedAt: '2026-01-01T00:00:00.000Z',
        completedAt: '2026-01-01T00:01:00.000Z',
        durationMs: 60000,
        tasks: [
          {
            id: 'integrate:prepare.1',
            title: 'Prepare branch',
            uses: 'mohist/prepare',
            status: 'failed',
            startedAt: '2026-01-01T00:00:00.000Z',
            completedAt: '2026-01-01T00:01:00.000Z',
            durationMs: 60000,
            attempts: 1,
            message: 'branch-invariant violation at start boundary for Prepare branch: expected branch \'mohist/run-wr-1\', observed \'master\'',
            output: JSON.stringify({
              kind: 'branch-invariant-violation',
              boundary: 'start',
              expectedBranch: 'mohist/run-wr-1',
              observedBranch: 'master',
            }),
          },
        ],
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

function makeBlockedIssue(blockedReason: string | undefined): Issue {
  return {
    number: 1,
    title: 'Split Integrate delivery',
    body: '',
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Integrate,
    health: IssueHealth.Blocked,
    blockedReason,
    projectId: 'test-project',
    labels: {},
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    comments: [],
    isDraft: false,
    canStart: false,
    blocker: null,
  }
}

function renderWorkflowView(issue: Issue) {
  return render(
    <WorkflowView
      issue={issue}
      timelineHook={() => ({ data: timelineData })}
    />,
  )
}

describe('WorkflowView delivery failure rendering', () => {
  it('shows the conflict kind, label, and next action when prepare fails with a conflict', async () => {
    timelineData = makeFailureTimeline('conflict')

    renderWorkflowView(makeBlockedIssue(undefined))

    const taskButton = await screen.findByRole('button', { name: /Prepare branch/ })
    fireEvent.click(taskButton)

    expect(await screen.findByText('Failure kind')).toBeInTheDocument()
    expect(screen.getByText('conflict')).toBeInTheDocument()
    expect(screen.getByText('Conflict needs attention')).toBeInTheDocument()
    expect(
      screen.getByText(/Inspect the conflicting files, resolve them on the issue branch/),
    ).toBeInTheDocument()
  })

  it('shows the base-moved kind, label, and next action when publish fails because the base moved', async () => {
    timelineData = makeFailureTimeline('base-moved')

    renderWorkflowView(makeBlockedIssue(undefined))

    const taskButton = await screen.findByRole('button', { name: /Publish changes/ })
    fireEvent.click(taskButton)

    expect(await screen.findByText('Failure kind')).toBeInTheDocument()
    expect(screen.getByText('base-moved')).toBeInTheDocument()
    expect(screen.getByText('Base branch moved')).toBeInTheDocument()
    expect(screen.getByText(/Prepare the branch again, then publish/)).toBeInTheDocument()
  })

  it('shows the retry-safe kind, label, and next action for transient delivery failures', async () => {
    timelineData = makeFailureTimeline('retry-safe')

    renderWorkflowView(makeBlockedIssue(undefined))

    const taskButton = await screen.findByRole('button', { name: /Prepare branch/ })
    fireEvent.click(taskButton)

    expect(await screen.findByText('Failure kind')).toBeInTheDocument()
    expect(screen.getByText('retry-safe')).toBeInTheDocument()
    expect(screen.getByText('Transient failure')).toBeInTheDocument()
    expect(screen.getByText(/Retry the task/)).toBeInTheDocument()
  })

  it('shows the branch-invariant-violation kind, runner/action attribution, and branch evidence', async () => {
    timelineData = makeBranchViolationTimeline()

    renderWorkflowView(makeBlockedIssue(undefined))

    const taskButton = await screen.findByRole('button', { name: /Prepare branch/ })
    fireEvent.click(taskButton)

    expect(await screen.findByText('Failure kind')).toBeInTheDocument()
    expect(screen.getByText('branch-invariant-violation')).toBeInTheDocument()
    expect(screen.getByText('Runner / action branch-invariant violation')).toBeInTheDocument()
    expect(
      screen.getByText(/This is a runner or action bug/),
    ).toBeInTheDocument()
    expect(screen.getByText('Attribution: runner/action (not issue work)')).toBeInTheDocument()
    // Boundary, expected, and observed are all rendered in the evidence block.
    const expectedRows = screen.getAllByText('mohist/run-wr-1')
    expect(expectedRows.length).toBeGreaterThan(0)
    expect(screen.getByText('master')).toBeInTheDocument()
  })

  it('does not render a delivery failure banner for the branch-invariant-violation kind on a non-delivery task', async () => {
    const timeline: WorkflowTimeline = {
      workflowRunId: 'workflow-run-1',
      status: 'failed',
      currentStage: WorkflowStage.Build,
      pendingWork: null,
      stages: [
        {
          stage: WorkflowStage.Build,
          status: 'failed',
          order: 2,
          startedAt: '2026-01-01T00:00:00.000Z',
          completedAt: '2026-01-01T00:01:00.000Z',
          durationMs: 60000,
          tasks: [
            {
              id: 'build-task-1',
              title: 'Implement WorkflowView',
              uses: 'mohist/coder-agent',
              status: 'failed',
              startedAt: '2026-01-01T00:00:00.000Z',
              completedAt: '2026-01-01T00:01:00.000Z',
              durationMs: 60000,
              attempts: 1,
              message: JSON.stringify({
                kind: 'branch-invariant-violation',
                boundary: 'start',
                expectedBranch: 'mohist/run-wr-1',
                observedBranch: 'master',
              }),
            },
          ],
          checks: [],
          approval: null,
        },
      ],
      availableActions: [],
    }
    timelineData = timeline

    renderWorkflowView(makeBlockedIssue('Build failed: agent crashed'))

    // Select the Build stage first so the failed task is visible.
    fireEvent.click(screen.getByRole('button', { name: 'Build' }))
    // Expand the failed task and assert no delivery banner appears.
    fireEvent.click(await screen.findByRole('button', { name: /Implement WorkflowView/ }))
    expect(screen.queryByText('Failure kind')).not.toBeInTheDocument()
    expect(
      screen.queryByText('branch-invariant-violation'),
    ).not.toBeInTheDocument()
  })

  it('does not show a delivery failure banner for non-delivery task failures', async () => {
    const timeline: WorkflowTimeline = {
      workflowRunId: 'workflow-run-2',
      status: 'failed',
      currentStage: WorkflowStage.Build,
      pendingWork: null,
      stages: [
        {
          stage: WorkflowStage.Build,
          status: 'failed',
          order: 2,
          startedAt: '2026-01-01T00:00:00.000Z',
          completedAt: '2026-01-01T00:01:00.000Z',
          durationMs: 60000,
          tasks: [
            {
              id: 'build-task-1',
              title: 'Implement WorkflowView',
              uses: 'mohist/coder-agent',
              status: 'failed',
              startedAt: '2026-01-01T00:00:00.000Z',
              completedAt: '2026-01-01T00:01:00.000Z',
              durationMs: 60000,
              attempts: 1,
              message: 'agent crashed',
            },
          ],
          checks: [],
          approval: null,
        },
      ],
      availableActions: [],
    }
    timelineData = timeline

    renderWorkflowView(makeBlockedIssue('Build failed: agent crashed'))

    fireEvent.click(screen.getByRole('button', { name: 'Build' }))
    await screen.findByRole('button', { name: /Implement WorkflowView/ })
    expect(screen.queryByText('Failure kind')).not.toBeInTheDocument()
  })

  it('surfaces the kind and next action in the Integrate failure panel from the blocked reason', () => {
    renderWorkflowView(makeBlockedIssue('Prepare failed (base-moved): non-fast-forward'))

    expect(screen.getByText('Integration Failed')).toBeInTheDocument()
    expect(screen.getByText('Prepare branch')).toBeInTheDocument()
    expect(screen.getByText('base-moved')).toBeInTheDocument()
    expect(screen.getByText('Base branch moved')).toBeInTheDocument()
    expect(screen.getByText(/Prepare the branch again, then publish/)).toBeInTheDocument()
  })

  it('surfaces the branch-invariant-violation kind in the Integrate failure panel with branch evidence', () => {
    renderWorkflowView(
      makeBlockedIssue(
        "branch-invariant violation at end boundary for Publish changes: expected branch 'mohist/run-wr-1', observed 'master'",
      ),
    )

    expect(screen.getByText('Integration Failed')).toBeInTheDocument()
    expect(screen.getByText('branch-invariant-violation')).toBeInTheDocument()
    expect(screen.getByText('Runner / action branch-invariant violation')).toBeInTheDocument()
    expect(screen.getByText('Attribution: runner/action (not issue work)')).toBeInTheDocument()
    // The evidence block surfaces the expected and observed branches.
    const expectedRows = screen.getAllByText('mohist/run-wr-1')
    expect(expectedRows.length).toBeGreaterThan(0)
    expect(screen.getByText('master')).toBeInTheDocument()
  })

  it.each(['workspace-setup'] as const)(
    'shows the %s kind, workflow-infrastructure attribution, and a distinct next action in the task banner',
    async (kind) => {
      timelineData = makeFailureTimeline(kind)

      renderWorkflowView(makeBlockedIssue(undefined))

      const taskButton = await screen.findByRole('button', { name: /Prepare branch/ })
      fireEvent.click(taskButton)

      expect(await screen.findByText('Failure kind')).toBeInTheDocument()
      expect(screen.getByText(kind)).toBeInTheDocument()
      expect(screen.getByText('Workflow workspace setup failure')).toBeInTheDocument()
      expect(
        screen.getByText('Attribution: workflow infrastructure (not issue work)'),
      ).toBeInTheDocument()
      // The workspace-setup failure must NOT reuse the existing
      // delivery-failure labels (conflict, base-moved, retry-safe,
      // branch-invariant-violation).
      expect(screen.queryByText('Conflict needs attention')).not.toBeInTheDocument()
      expect(screen.queryByText('Base branch moved')).not.toBeInTheDocument()
      expect(screen.queryByText('Transient failure')).not.toBeInTheDocument()
      expect(
        screen.queryByText('Runner / action branch-invariant violation'),
      ).not.toBeInTheDocument()
      expect(
        screen.queryByText('Attribution: runner/action (not issue work)'),
      ).not.toBeInTheDocument()
    },
  )

  it('renders the workspace-setup evidence (workspace path) in the banner', async () => {
    const timeline: WorkflowTimeline = {
      workflowRunId: 'wr_abc',
      status: 'failed',
      currentStage: WorkflowStage.Integrate,
      pendingWork: null,
      stages: [
        {
          stage: WorkflowStage.Integrate,
          status: 'failed',
          order: 4,
          startedAt: '2026-01-01T00:00:00.000Z',
          completedAt: '2026-01-01T00:01:00.000Z',
          durationMs: 60000,
          tasks: [
            {
              id: 'integrate:prepare.1',
              title: 'Prepare branch',
              uses: 'mohist/prepare',
              status: 'failed',
              startedAt: '2026-01-01T00:00:00.000Z',
              completedAt: '2026-01-01T00:01:00.000Z',
              durationMs: 60000,
              attempts: 1,
              message: 'could not prepare workflow workspace (workspace-setup): git clone failed for ...',
              output: JSON.stringify({
                kind: 'workspace-setup',
                workspacePath: '/home/runner/workspaces/proj_x/wr_abc',
              }),
            },
          ],
          checks: [],
          approval: null,
        },
      ],
      availableActions: [],
    }
    timelineData = timeline

    renderWorkflowView(makeBlockedIssue(undefined))

    const taskButton = await screen.findByRole('button', { name: /Prepare branch/ })
    fireEvent.click(taskButton)

    expect(await screen.findByText('Failure kind')).toBeInTheDocument()
    expect(screen.getByText('workspace-setup')).toBeInTheDocument()
    expect(
      screen.getByText('Attribution: workflow infrastructure (not issue work)'),
    ).toBeInTheDocument()
    expect(screen.getByText('/home/runner/workspaces/proj_x/wr_abc')).toBeInTheDocument()
  })
})
