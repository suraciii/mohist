import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import {
  PrDeliveryIndicator,
  PrDeliverySummary,
  findPublishViaPrMetadata,
  findPullRequestDeliveryMetadata,
  isCompletedPublishViaPrTask,
  isCompletedPullRequestDeliveryTask,
} from './PrDeliveryIndicator'
import type { WorkflowTimeline } from '../../../entities/issue/model/workflow-timeline'

const sampleMetadata = {
  prNumber: 42,
  prUrl: 'https://github.com/acme/widgets/pull/42',
  mergeCommitSha: 'abc123',
  targetBranch: 'main',
}

describe('PrDeliveryIndicator', () => {
  afterEach(() => cleanup())

  it('renders the indicator with the PR number and a link to the PR URL', () => {
    render(<PrDeliveryIndicator metadata={sampleMetadata} />)
    const link = screen.getByTestId('pr-delivery-indicator')
    expect(link.getAttribute('href')).toBe(sampleMetadata.prUrl)
    expect(link.getAttribute('target')).toBe('_blank')
    expect(link.getAttribute('rel')).toBe('noopener noreferrer')
    expect(link.getAttribute('data-pr-number')).toBe('42')
    expect(link.getAttribute('data-pr-url')).toBe(sampleMetadata.prUrl)
    expect(link.textContent).toContain('经由 PR')
    expect(link.textContent).toContain('#42')
    expect(link.textContent).toContain('合并')
  })
})

describe('PrDeliverySummary', () => {
  afterEach(() => cleanup())

  it('renders nothing when timeline is missing', () => {
    const { container } = render(<PrDeliverySummary timeline={null} />)
    expect(container.firstChild).toBeNull()
  })

  it('renders nothing when no completed publish-via-pr task is present', () => {
    const timeline = makeTimeline([])
    const { container } = render(<PrDeliverySummary timeline={timeline} />)
    expect(container.firstChild).toBeNull()
  })

  it('renders the indicator when a completed publish-via-pr task has prNumber/prUrl', () => {
    const timeline = makeTimeline([
      {
        id: 'publish.1',
        title: 'Publish via PR',
        uses: 'mohist/publish-via-pr',
        status: 'completed',
        output: {
          kind: 'publish-via-pr',
          prNumber: 7,
          prUrl: 'https://github.com/acme/widgets/pull/7',
          mergeCommitSha: 'cafef00d',
          targetBranch: 'main',
        },
      },
    ])
    render(<PrDeliverySummary timeline={timeline} />)
    const link = screen.getByTestId('pr-delivery-indicator')
    expect(link.getAttribute('href')).toBe('https://github.com/acme/widgets/pull/7')
    expect(link.textContent).toContain('#7')
  })

  it('does not render for a create-pull-request task whose output lacks prNumber/prUrl', () => {
    const timeline = makeTimeline([
      {
        id: 'open-pr.1',
        title: 'Open or update GitHub PR',
        uses: 'mohist/create-pull-request',
        status: 'completed',
        output: {
          kind: 'create-pull-request',
          targetBranch: 'main',
        },
      },
    ])
    const { container } = render(<PrDeliverySummary timeline={timeline} />)
    expect(container.firstChild).toBeNull()
  })

  it('renders the indicator from a create-pull-request task output with prNumber/prUrl', () => {
    const timeline = makeTimeline([
      {
        id: 'open-pr.1',
        title: 'Open or update GitHub PR',
        uses: 'mohist/create-pull-request',
        status: 'completed',
        output: {
          kind: 'create-pull-request',
          prNumber: 8,
          prUrl: 'https://github.com/acme/widgets/pull/8',
          targetBranch: 'main',
        },
      },
    ])
    render(<PrDeliverySummary timeline={timeline} />)
    const link = screen.getByTestId('pr-delivery-indicator')
    expect(link.getAttribute('href')).toBe('https://github.com/acme/widgets/pull/8')
    expect(link.getAttribute('data-pr-number')).toBe('8')
    expect(link.textContent).toContain('#8')
  })

  it('does not render for direct-push mohist/publish task', () => {
    const timeline = makeTimeline([
      {
        id: 'publish.1',
        title: 'Publish',
        uses: 'mohist/publish',
        status: 'completed',
        output: { kind: 'publish', commit: 'deadbeef' },
      },
    ])
    const { container } = render(<PrDeliverySummary timeline={timeline} />)
    expect(container.firstChild).toBeNull()
  })

  it('does not render when publish-via-pr is still running', () => {
    const timeline = makeTimeline([
      {
        id: 'publish.1',
        title: 'Publish via PR',
        uses: 'mohist/publish-via-pr',
        status: 'running',
        output: { kind: 'publish-via-pr', prNumber: 7, prUrl: 'https://x' },
      },
    ])
    const { container } = render(<PrDeliverySummary timeline={timeline} />)
    expect(container.firstChild).toBeNull()
  })

  it('does not render when output is missing', () => {
    const timeline = makeTimeline([
      {
        id: 'publish.1',
        title: 'Publish via PR',
        uses: 'mohist/publish-via-pr',
        status: 'completed',
        output: null,
      },
    ])
    const { container } = render(<PrDeliverySummary timeline={timeline} />)
    expect(container.firstChild).toBeNull()
  })

  it('does not render when output is missing prNumber or prUrl', () => {
    const timeline = makeTimeline([
      {
        id: 'publish.1',
        title: 'Publish via PR',
        uses: 'mohist/publish-via-pr',
        status: 'completed',
        output: { kind: 'publish-via-pr', prNumber: 7 },
      },
    ])
    const { container } = render(<PrDeliverySummary timeline={timeline} />)
    expect(container.firstChild).toBeNull()
  })

  it('finds the first completed publish-via-pr task across multiple stages', () => {
    const timeline: WorkflowTimeline = {
      workflowRunId: 'wr-1',
      status: 'completed',
      currentStage: 'done',
      pendingWork: null,
      stages: [
        {
          stage: 'plan' as never,
          status: 'completed',
          order: 0,
          startedAt: null,
          completedAt: null,
          durationMs: null,
          tasks: [],
          checks: [],
          approval: null,
        },
        {
          stage: 'integrate' as never,
          status: 'completed',
          order: 3,
          startedAt: null,
          completedAt: null,
          durationMs: null,
          tasks: [
            {
              id: 'publish.1',
              title: 'Publish via PR',
              uses: 'mohist/publish-via-pr',
              status: 'completed',
              startedAt: null,
              completedAt: null,
              durationMs: null,
              attempts: 1,
              message: null,
              output: { kind: 'publish-via-pr', prNumber: 9, prUrl: 'https://x/9' },
            },
          ],
          checks: [],
          approval: null,
        },
      ],
      availableActions: [],
    }
    const metadata = findPublishViaPrMetadata(timeline)
    expect(metadata).not.toBeNull()
    expect(metadata!.prNumber).toBe(9)
  })

  it('finds completed split PR task metadata', () => {
    const timeline = makeTimeline([
      {
        id: 'merge-pr.1',
        title: 'Merge GitHub PR',
        uses: 'mohist/merge-pull-request',
        status: 'completed',
        output: { kind: 'merge-pull-request', prNumber: 11, prUrl: 'https://x/11' },
      },
    ])
    const metadata = findPullRequestDeliveryMetadata(timeline)
    expect(metadata).not.toBeNull()
    expect(metadata!.prNumber).toBe(11)
  })

  it('renders the indicator from a build-stage create-pull-request task before integrate', () => {
    const timeline = makeMultiStageTimeline([
      {
        stage: 'build' as never,
        tasks: [
          {
            id: 'build:open-pr',
            title: 'Open or update GitHub PR',
            uses: 'mohist/create-pull-request',
            status: 'completed',
            output: {
              kind: 'create-pull-request',
              prNumber: 17,
              prUrl: 'https://github.com/acme/widgets/pull/17',
              targetBranch: 'main',
            },
          },
        ],
      },
      { stage: 'check' as never, tasks: [] },
      { stage: 'integrate' as never, tasks: [] },
    ])
    render(<PrDeliverySummary timeline={timeline} />)
    const link = screen.getByTestId('pr-delivery-indicator')
    expect(link.getAttribute('href')).toBe('https://github.com/acme/widgets/pull/17')
    expect(link.getAttribute('data-pr-number')).toBe('17')
    expect(link.textContent).toContain('#17')
  })

  it('prefers the integrate-stage merge-pull-request metadata when build create and integrate merge both completed', () => {
    const timeline = makeMultiStageTimeline([
      {
        stage: 'build' as never,
        tasks: [
          {
            id: 'build:open-pr',
            title: 'Open or update GitHub PR',
            uses: 'mohist/create-pull-request',
            status: 'completed',
            output: {
              kind: 'create-pull-request',
              prNumber: 21,
              prUrl: 'https://github.com/acme/widgets/pull/21',
              targetBranch: 'main',
            },
          },
          {
            id: 'build:update-pr',
            title: 'Update GitHub PR with build results',
            uses: 'mohist/create-pull-request',
            status: 'completed',
            output: {
              kind: 'create-pull-request',
              prNumber: 21,
              prUrl: 'https://github.com/acme/widgets/pull/21',
              targetBranch: 'main',
            },
          },
        ],
      },
      { stage: 'check' as never, tasks: [] },
      {
        stage: 'integrate' as never,
        tasks: [
          {
            id: 'integrate:merge-pr',
            title: 'Merge GitHub PR',
            uses: 'mohist/merge-pull-request',
            status: 'completed',
            output: {
              kind: 'merge-pull-request',
              prNumber: 21,
              prUrl: 'https://github.com/acme/widgets/pull/21',
              mergeCommitSha: 'final-sha',
              targetBranch: 'main',
            },
          },
        ],
      },
    ])
    render(<PrDeliverySummary timeline={timeline} />)
    const link = screen.getByTestId('pr-delivery-indicator')
    expect(link.getAttribute('href')).toBe('https://github.com/acme/widgets/pull/21')
    expect(link.getAttribute('data-pr-number')).toBe('21')
    const metadata = findPullRequestDeliveryMetadata(timeline)
    expect(metadata).not.toBeNull()
    expect(metadata!.mergeCommitSha).toBe('final-sha')
  })

  it('keeps the build-stage create metadata when only build create tasks completed (integrate pending)', () => {
    const timeline = makeMultiStageTimeline([
      {
        stage: 'build' as never,
        tasks: [
          {
            id: 'build:open-pr',
            title: 'Open or update GitHub PR',
            uses: 'mohist/create-pull-request',
            status: 'completed',
            output: {
              kind: 'create-pull-request',
              prNumber: 33,
              prUrl: 'https://github.com/acme/widgets/pull/33',
              targetBranch: 'main',
            },
          },
          {
            id: 'build:update-pr',
            title: 'Update GitHub PR with build results',
            uses: 'mohist/create-pull-request',
            status: 'completed',
            output: {
              kind: 'create-pull-request',
              prNumber: 33,
              prUrl: 'https://github.com/acme/widgets/pull/33',
              targetBranch: 'main',
            },
          },
        ],
      },
      { stage: 'check' as never, tasks: [] },
      { stage: 'integrate' as never, tasks: [] },
    ])
    const metadata = findPullRequestDeliveryMetadata(timeline)
    expect(metadata).not.toBeNull()
    expect(metadata!.prNumber).toBe(33)
    expect(metadata!.prUrl).toBe('https://github.com/acme/widgets/pull/33')
  })

  it('ignores build-stage create-pull-request tasks that have not yet completed', () => {
    const timeline = makeMultiStageTimeline([
      {
        stage: 'build' as never,
        tasks: [
          {
            id: 'build:open-pr',
            title: 'Open or update GitHub PR',
            uses: 'mohist/create-pull-request',
            status: 'running',
            output: null,
          },
        ],
      },
      { stage: 'check' as never, tasks: [] },
      { stage: 'integrate' as never, tasks: [] },
    ])
    const { container } = render(<PrDeliverySummary timeline={timeline} />)
    expect(container.firstChild).toBeNull()
  })
})

describe('isCompletedPublishViaPrTask', () => {
  it('returns true only for completed publish-via-pr tasks', () => {
    expect(
      isCompletedPublishViaPrTask({
        id: 'publish.1',
        title: 'Publish via PR',
        uses: 'mohist/publish-via-pr',
        status: 'completed',
        startedAt: null,
        completedAt: null,
        durationMs: null,
        attempts: 1,
        message: null,
      }),
    ).toBe(true)
    expect(
      isCompletedPublishViaPrTask({
        id: 'publish.1',
        title: 'Publish via PR',
        uses: 'mohist/publish-via-pr',
        status: 'running',
        startedAt: null,
        completedAt: null,
        durationMs: null,
        attempts: 1,
        message: null,
      }),
    ).toBe(false)
    expect(
      isCompletedPublishViaPrTask({
        id: 'publish.1',
        title: 'Publish',
        uses: 'mohist/publish',
        status: 'completed',
        startedAt: null,
        completedAt: null,
        durationMs: null,
        attempts: 1,
        message: null,
      }),
    ).toBe(false)
  })

  it('recognises completed create / merge PR delivery tasks through the helper', () => {
    expect(
      isCompletedPullRequestDeliveryTask({
        id: 'open-pr.1',
        title: 'Open or update GitHub PR',
        uses: 'mohist/create-pull-request',
        status: 'completed',
        startedAt: null,
        completedAt: null,
        durationMs: null,
        attempts: 1,
        message: null,
      }),
    ).toBe(true)
    expect(
      isCompletedPullRequestDeliveryTask({
        id: 'merge-pr.1',
        title: 'Merge GitHub PR',
        uses: 'mohist/merge-pull-request',
        status: 'completed',
        startedAt: null,
        completedAt: null,
        durationMs: null,
        attempts: 1,
        message: null,
      }),
    ).toBe(true)
    expect(
      isCompletedPullRequestDeliveryTask({
        id: 'open-pr.1',
        title: 'Open or update GitHub PR',
        uses: 'mohist/create-pull-request',
        status: 'running',
        startedAt: null,
        completedAt: null,
        durationMs: null,
        attempts: 1,
        message: null,
      }),
    ).toBe(false)
    expect(
      isCompletedPullRequestDeliveryTask({
        id: 'build-task.1',
        title: 'Implement feature',
        uses: 'mohist/coder-agent',
        status: 'completed',
        startedAt: null,
        completedAt: null,
        durationMs: null,
        attempts: 1,
        message: null,
      }),
    ).toBe(false)
  })
})

interface TaskFixture {
  id: string
  title: string
  uses: string
  status: 'completed' | 'running' | 'failed' | 'pending' | 'skipped'
  output: Record<string, unknown> | null
}

function makeTimeline(tasks: TaskFixture[]): WorkflowTimeline {
  return {
    workflowRunId: 'wr-1',
    status: 'completed',
    currentStage: 'integrate',
    pendingWork: null,
    stages: [
      {
        stage: 'integrate' as never,
        status: 'completed',
        order: 3,
        startedAt: null,
        completedAt: null,
        durationMs: null,
        tasks: tasks.map((t) => ({
          id: t.id,
          title: t.title,
          uses: t.uses,
          status: t.status,
          startedAt: null,
          completedAt: null,
          durationMs: null,
          attempts: 1,
          message: null,
          output: t.output,
        })),
        checks: [],
        approval: null,
      },
    ],
    availableActions: [],
  }
}

interface MultiStageFixture {
  stage: 'build' | 'check' | 'integrate' | 'plan'
  tasks: TaskFixture[]
}

function makeMultiStageTimeline(stages: MultiStageFixture[]): WorkflowTimeline {
  const orderByStage: Record<MultiStageFixture['stage'], number> = {
    plan: 0,
    build: 1,
    check: 2,
    integrate: 3,
  }
  return {
    workflowRunId: 'wr-1',
    status: 'completed',
    currentStage: 'integrate',
    pendingWork: null,
    stages: stages.map((s) => ({
      stage: s.stage as never,
      status: 'completed' as const,
      order: orderByStage[s.stage],
      startedAt: null,
      completedAt: null,
      durationMs: null,
      tasks: s.tasks.map((t) => ({
        id: t.id,
        title: t.title,
        uses: t.uses,
        status: t.status,
        startedAt: null,
        completedAt: null,
        durationMs: null,
        attempts: 1,
        message: null,
        output: t.output,
      })),
      checks: [],
      approval: null,
    })),
    availableActions: [],
  }
}
