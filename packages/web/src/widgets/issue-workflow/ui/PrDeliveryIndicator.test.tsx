// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { PrDeliveryIndicator, PrDeliverySummary, findPublishViaPrMetadata, isCompletedPublishViaPrTask } from './PrDeliveryIndicator'
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
        output: JSON.stringify({
          kind: 'publish-via-pr',
          prNumber: 7,
          prUrl: 'https://github.com/acme/widgets/pull/7',
          mergeCommitSha: 'cafef00d',
          targetBranch: 'main',
        }),
      },
    ])
    render(<PrDeliverySummary timeline={timeline} />)
    const link = screen.getByTestId('pr-delivery-indicator')
    expect(link.getAttribute('href')).toBe('https://github.com/acme/widgets/pull/7')
    expect(link.textContent).toContain('#7')
  })

  it('does not render for direct-push mohist/publish task', () => {
    const timeline = makeTimeline([
      {
        id: 'publish.1',
        title: 'Publish',
        uses: 'mohist/publish',
        status: 'completed',
        output: JSON.stringify({ kind: 'publish', commit: 'deadbeef' }),
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
        output: JSON.stringify({ kind: 'publish-via-pr', prNumber: 7, prUrl: 'https://x' }),
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
        output: JSON.stringify({ kind: 'publish-via-pr', prNumber: 7 }),
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
              output: JSON.stringify({ kind: 'publish-via-pr', prNumber: 9, prUrl: 'https://x/9' }),
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
})

interface TaskFixture {
  id: string
  title: string
  uses: string
  status: 'completed' | 'running' | 'failed' | 'pending' | 'skipped'
  output: string | null
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