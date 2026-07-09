// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, screen, waitFor, within } from '@testing-library/react'
import {
  DEFAULT_RECOVERY,
  enabledString,
  makeIssue,
  mockMatchMedia,
  renderPage,
} from './_issueDetailReferenceRailTestUtils'

const mockUseIssueDiff = vi.fn()
const mockUseIssueCommits = vi.fn()
const mockUseWorkflowTimeline = vi.fn()
const mockUseWorkflowYaml = vi.fn()
const mockUseAgentStatus = vi.fn()
const mockUseIssue = vi.fn()
const mockUseWorkspaceStatus = vi.fn()

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssue: (...args: unknown[]) => mockUseIssue(...args),
    useIssueDiff: (...args: unknown[]) => mockUseIssueDiff(...args),
    useIssueCommits: (...args: unknown[]) => mockUseIssueCommits(...args),
    useWorkflowTimeline: (...args: unknown[]) => mockUseWorkflowTimeline(...args),
    useWorkflowYaml: (...args: unknown[]) => mockUseWorkflowYaml(...args),
    useWorkspaceStatus: (...args: unknown[]) => mockUseWorkspaceStatus(...args),
    useIssueEvents: () => ({ data: undefined, isLoading: false }),
    getIssueWorkflowVariables: vi.fn(() => Promise.resolve({ vars: {}, stages: {} })),
  }
})

vi.mock('../../../entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/settings')>()
  return {
    ...actual,
    useWorkflowProfiles: () => ({ data: [] }),
    useAvailableModelIds: () => ({ data: [] }),
    useOpencodeModel: () => ({ data: null }),
    useModelVariants: () => ({ data: [] }),
    useEffectiveDefaultWorkflowProfile: () => ({ data: null }),
  }
})

vi.mock('../../../widgets/issue-event-timeline/ui/EventTimelinePanel', () => ({
  EventTimelinePanel: vi.fn((props: { issueNumber: number; issueId?: string | null; workflowStatus?: string | null; enabled?: boolean }) => (
    <div
      data-testid="event-timeline-panel-mock"
      data-issue-number={props.issueNumber}
      data-issue-id={props.issueId ?? ''}
      data-workflow-status={props.workflowStatus ?? ''}
      data-enabled={enabledString(props.enabled)}
    />
  )),
}))

vi.mock('../../../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: (...args: unknown[]) => mockUseAgentStatus(...args),
  }
})

describe('IssueDetailPage reference-rail — lightest visual weight', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('ranks rail below reading flow and reading flow below status headline by data-tier-weight', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    const readingFlow = screen.getByTestId('reading-flow')
    const referenceRail = screen.getByTestId('reference-rail')

    const tierOrder = { 'status-header': 3, 'reading-flow': 2, 'reference-rail': 1 } as const
    const headlineWeight = tierOrder[headline.dataset.tierWeight as keyof typeof tierOrder]
    const flowWeight = tierOrder[readingFlow.dataset.tierWeight as keyof typeof tierOrder]
    const railWeight = tierOrder[referenceRail.dataset.tierWeight as keyof typeof tierOrder]
    expect(headlineWeight).toBeGreaterThan(flowWeight)
    expect(flowWeight).toBeGreaterThan(railWeight)
  })

  it('does not place sticky or heavy-fill chrome on the reference rail', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.querySelector('[data-sticky="true"]')).toBeNull()
    expect(referenceRail.className).not.toMatch(/bg-(info|warning|danger|success)-subtle/)
    expect(referenceRail.className).not.toMatch(/\bsticky\b/)
  })

  it('does not nest same-name CardSection chrome inside expanded rail cards', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({
        model: 'sonnet',
        repository: {
          name: 'master',
          baseBranch: 'master',
          gitUrl: 'https://github.com/suraciii/mohist.git',
        },
        recovery: DEFAULT_RECOVERY,
      }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const railCards = Array.from(referenceRail.querySelectorAll('[data-rail-card="collapsible"]'))
    expect(railCards.length).toBeGreaterThan(0)

    for (const card of railCards) {
      const body = card.querySelector('[data-testid$="-body"]')
      if (!body) continue
      const nestedSections = body.querySelectorAll('section.rounded-lg.border')
      expect(nestedSections).toHaveLength(0)
    }

    expect(within(screen.getByTestId('reference-rail-details')).queryByRole('heading', { name: 'Details' })).toBeNull()
    expect(within(screen.getByTestId('reference-rail-workflow-profile')).queryAllByText('Workflow Profile')).toHaveLength(1)
    expect(within(screen.getByTestId('reference-rail-actions')).queryByRole('heading', { name: 'Actions' })).toBeNull()
  })
})

describe('IssueDetailPage reference-rail — lightest visual weight', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMatchMedia(false)
    mockUseWorkflowYaml.mockReturnValue({ data: undefined, isLoading: false })
    mockUseAgentStatus.mockReturnValue({ data: { activeAgents: [], capacity: { max: 1 }, runnerAvailable: true } })
    mockUseWorkspaceStatus.mockReturnValue({ data: undefined, isLoading: false })
    mockUseIssueDiff.mockReturnValue({ data: undefined })
    mockUseIssueCommits.mockReturnValue({ data: undefined })
    mockUseWorkflowTimeline.mockReturnValue({ data: undefined })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('does not apply heavy-fill or shadow chrome to the rail container or its cards', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.className).not.toMatch(/shadow/)
    expect(referenceRail.className).not.toMatch(/bg-(info|warning|danger|success)-subtle/)

    const railCards = Array.from(referenceRail.querySelectorAll('[data-rail-card="collapsible"]'))
    expect(railCards.length).toBeGreaterThan(0)
    for (const card of railCards) {
      expect(card.className).not.toMatch(/bg-(info|warning|danger|success)-subtle/)
      expect(card.className).not.toMatch(/shadow/)
    }
  })

  it('uses muted text color on rail toggle buttons (lighter than the headline and reading flow)', async () => {
    mockUseIssue.mockReturnValue({
      data: makeIssue({ recovery: DEFAULT_RECOVERY }),
      isLoading: false,
      isError: false,
    })

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const detailsToggle = screen.getByTestId('reference-rail-details-toggle')
    expect(detailsToggle.className).toMatch(/text-muted-foreground/)
    expect(detailsToggle.className).not.toMatch(/text-foreground(\b|[^/])/)
    expect(referenceRail.contains(detailsToggle)).toBe(true)
  })
})
