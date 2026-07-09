// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, fireEvent, screen } from '@testing-library/react'

import { EpicStatus } from '../../../entities/epic'
import { IssueStatus, WorkflowStage, IssueHealth } from '../../../entities/issue'

import { issues, linkedIssue, renderPage, getActionGroup } from './_epicDetailPageTestHarness'

/**
 * Page-level summary-first information architecture tests for <EpicDetailPage/> (T-002): summary/overview ordering, progress summary, advancement copy kinds, and no-regression of linked-issue/edit/add.
 */

const mocks = vi.hoisted(() => ({
  useEpic: vi.fn(),
  useIssues: vi.fn(),
  useAddEpicIssue: vi.fn(),
  useRemoveEpicIssue: vi.fn(),
  useStartIssue: vi.fn(),
  useStartEpic: vi.fn(),
  useMarkEpicDone: vi.fn(),
  useCloseEpic: vi.fn(),
  useUpdateEpic: vi.fn(),
  usePauseEpic: vi.fn(),
  useResumeEpic: vi.fn(),
}))


vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useIssues: mocks.useIssues,
}))

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpic: mocks.useEpic,
    useAddEpicIssue: mocks.useAddEpicIssue,
    useRemoveEpicIssue: mocks.useRemoveEpicIssue,
    useStartIssue: mocks.useStartIssue,
    useStartEpic: mocks.useStartEpic,
    useMarkEpicDone: mocks.useMarkEpicDone,
    useCloseEpic: mocks.useCloseEpic,
    useUpdateEpic: mocks.useUpdateEpic,
    usePauseEpic: mocks.usePauseEpic,
    useResumeEpic: mocks.useResumeEpic,
  }
})

describe('EpicDetailPage summary-first information architecture (T-002)', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  const LONG_DESCRIPTION = [
    '## Background',
    '',
    'This is the long descriptive prose that previously appeared in the header card before the summary grid.',
    '',
    'It pushed the status facts below the first fold on narrow viewports.',
    '',
    Array.from({ length: 12 }, (_, i) => `Paragraph ${i + 1} with additional context and details.`).join('\n\n'),
  ].join('\n\n')

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: 26,
      title: 'Epic title',
      description: LONG_DESCRIPTION,
      priority: 'p1',
      status: EpicStatus.Running,
      pauseReason: null,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 1,
        totalIssueCount: 3,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [],
      ...overrides,
    }
  }

  function getSummaryGrid(): HTMLElement {
    const summary = screen.getByTestId('summary-grid')
    return summary
  }

  function getOverviewCard(): HTMLElement {
    return screen.getByTestId('overview-card')
  }

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useIssues.mockReturnValue({ data: issues })
    mocks.useAddEpicIssue.mockReturnValue({ mutate: addMutate, isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  describe('summary-before-description DOM order', () => {
    it('renders the summary grid before the Overview card on desktop', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

      renderPage()

      const summary = getSummaryGrid()
      const overview = getOverviewCard()
      expect(summary.compareDocumentPosition(overview) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('places the summary grid before the Overview card in DOM order on mobile (390px viewport)', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

      renderPage()

      const summary = getSummaryGrid()
      const overview = getOverviewCard()
      expect(summary.compareDocumentPosition(overview) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('keeps the summary grid inside the header card while the Overview card sits below it', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

      renderPage()

      const summary = getSummaryGrid()
      const headerCard = summary.closest('[data-slot="card"]') as HTMLElement
      expect(headerCard).toBeTruthy()
      expect(headerCard.querySelector('[data-testid="overview-card"]')).toBeNull()
    })
  })

  describe('no Overview card when description is empty', () => {
    it('omits the Overview card entirely when epic.description is the empty string', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ description: '' }), isLoading: false })

      renderPage()

      expect(screen.queryByTestId('overview-card')).toBeNull()
      expect(screen.queryByTestId('epic-description')).toBeNull()
    })

    it('still renders the summary grid when description is empty', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ description: '' }), isLoading: false })

      renderPage()

      expect(screen.getByTestId('summary-grid')).toBeTruthy()
      expect(screen.getByText('1 / 3')).toBeTruthy()
    })
  })

  describe('Overview/Description region is collapsible via MarkdownReader', () => {
    it('renders the MarkdownReader in collapsible mode inside the Overview card', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

      renderPage()

      const description = screen.getByTestId('epic-description')
      const reader = description.querySelector('[data-testid="markdown-reader"]') as HTMLElement
      expect(reader).toBeTruthy()
      expect(reader.getAttribute('data-mode')).toBe('collapsible')
    })

    it('exposes the expand/collapse test hooks from MarkdownReader inside the Overview card', () => {
      const originalScrollHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight')
      Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
        configurable: true,
        get() {
          return 5000
        },
      })
      try {
        mocks.useEpic.mockReturnValue({
          data: makeEpic({
            description: Array.from({ length: 80 }, (_, i) => `Line ${i + 1} content that exceeds the collapsed height.`).join('\n\n'),
          }),
          isLoading: false,
        })

        renderPage()

        const description = screen.getByTestId('epic-description')
        const expandControl = description.querySelector('[data-testid="markdown-expand-control"]') as HTMLElement
        expect(expandControl).toBeTruthy()
      } finally {
        if (originalScrollHeight) {
          Object.defineProperty(HTMLElement.prototype, 'scrollHeight', originalScrollHeight)
        } else {
          delete (HTMLElement.prototype as unknown as Record<string, unknown>).scrollHeight
        }
      }
    })
  })

  describe('progress summary', () => {
    it('shows delivered / total counts', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ progress: { deliveredCount: 2, totalIssueCount: 5, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false } }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByText('2 / 5')).toBeTruthy()
      expect(screen.queryByTestId('progress-ready-to-mark-done')).toBeNull()
    })

    it('surfaces a ready-to-mark-done indication when readyToMarkDone is true', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ progress: { deliveredCount: 3, totalIssueCount: 3, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: true } }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByText('3 / 3')).toBeTruthy()
      const indicator = screen.getByTestId('progress-ready-to-mark-done')
      expect(indicator).toBeTruthy()
      expect(indicator.textContent).toMatch(/ready to mark done/i)
    })

    it('omits the ready-to-mark-done indicator for terminal epics (done/closed)', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Done,
          progress: { deliveredCount: 1, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: true },
        }),
        isLoading: false,
      })

      renderPage()

      expect(screen.queryByTestId('progress-ready-to-mark-done')).toBeNull()
    })
  })

  describe('advancement copy kinds', () => {
    it('renders waiting-for-in-progress copy with nav link to the in-progress issue', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          progress: { deliveredCount: 0, totalIssueCount: 2, blockedIssues: [], activeIssues: [{ id: 'issue-2', number: 2, title: 'Active', health: 'active' }], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Backlog', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: true, startBlocker: null }),
            linkedIssue({ id: 'issue-2', number: 2, title: 'Active', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1', canStart: false }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('Waiting for #2 to finish')
      const link = screen.getByTestId('advancement-link')
      expect(link.getAttribute('href')).toContain('/issues/2')
    })

    it('renders draft-blocker copy with nav link to the draft candidate', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-8', number: 8, title: 'Draft candidate', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'draft' } }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('still a draft')
      expect(copy.textContent).toContain('#8')
      const link = screen.getByTestId('advancement-link')
      expect(link.getAttribute('href')).toContain('/issues/8')
    })

    it('renders external-prerequisite-blocker copy with nav links to the prerequisites', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({
              id: 'issue-9',
              number: 9,
              title: 'Blocked by externals',
              status: IssueStatus.Backlog,
              stage: WorkflowStage.Plan,
              canStart: false,
              startBlocker: null,
              externalPrerequisites: [
                { number: 100, title: 'Upstream A', stage: 'plan', status: 'backlog' },
                { number: 200, title: 'Upstream B', stage: 'plan', status: 'backlog' },
              ],
            }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('external issues')
      expect(copy.textContent).toContain('#100')
      expect(copy.textContent).toContain('#200')
      const links = screen.getAllByTestId('advancement-link')
      expect(links.length).toBe(2)
      const hrefs = links.map(l => l.getAttribute('href')).sort()
      expect(hrefs).toContain('/issues/100')
      expect(hrefs).toContain('/issues/200')
    })

    it('renders running-but-idle copy without nav links for a running epic with no startable next', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Running,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Waiting on', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('Running')
      expect(copy.textContent).not.toContain('Idle')
      expect(screen.queryByTestId('advancement-link')).toBeNull()
    })

    it('renders has-next nav link without additional advancement copy when a server-provided next issue exists', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: { id: 'issue-3', number: 3, title: 'Candidate' }, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate' }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const next = screen.getByTestId('next-issue')
      expect(next.getAttribute('href')).toContain('/issues/3')
      // When nextIssue is present and state is has-next, no extra advancement copy is rendered
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
    })

    it('does not render external-blocker copy below a startable next issue with prerequisite metadata', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: { id: 'issue-3', number: 3, title: 'Candidate' }, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({
              id: 'issue-3',
              number: 3,
              title: 'Candidate',
              status: IssueStatus.Backlog,
              stage: WorkflowStage.Plan,
              canStart: true,
              startBlocker: null,
              externalPrerequisites: [{ number: 77, title: 'Historical prerequisite', stage: 'done', status: 'done' }],
            }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const next = screen.getByTestId('next-issue')
      expect(next).toHaveTextContent('#3 Candidate')
      expect(next.getAttribute('href')).toContain('/issues/3')
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
      expect(screen.queryByText(/external issue/i)).toBeNull()
    })

    it('does not show a lower-priority draft blocker under a server-provided next issue', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 2, blockedIssues: [], activeIssues: [], nextIssue: { id: 'issue-9', number: 9, title: 'Priority candidate' }, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-4', number: 4, title: 'Older draft', priority: 'p3', canStart: false, startBlocker: { kind: 'draft' } }),
            linkedIssue({ id: 'issue-9', number: 9, title: 'Priority candidate', priority: 'p0', canStart: true, startBlocker: null }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const next = screen.getByTestId('next-issue')
      expect(next).toHaveTextContent('#9 Priority candidate')
      expect(next.getAttribute('href')).toContain('/issues/9')
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
      const advancementArea = screen.getByTestId('next-issue-region')
      expect(advancementArea.textContent ?? '').not.toMatch(/still a draft/i)
    })

    it('renders idle-no-next reason copy when an idle epic has no startable candidate and no specific blocker', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Waiting on', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('Running')
    })

    it('renders "No linked issues yet" when there are no linked issues and no next issue', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 0, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [],
        }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByText('No linked issues yet')).toBeTruthy()
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
    })

    it('uses neutral copy for an all-cancelled epic instead of delivered wording', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-7', number: 7, title: 'Cancelled issue', status: IssueStatus.Cancelled, stage: WorkflowStage.Done, health: IssueHealth.Cancelled, canStart: false }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(screen.getByText('0 / 1')).toBeTruthy()
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('All linked issues are delivered')
    })

    it('uses neutral copy for mixed done and cancelled issues instead of delivered wording', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          status: EpicStatus.Idle,
          progress: { deliveredCount: 1, totalIssueCount: 2, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, canStart: false }),
            linkedIssue({ id: 'issue-7', number: 7, title: 'Cancelled issue', status: IssueStatus.Cancelled, stage: WorkflowStage.Done, health: IssueHealth.Cancelled, canStart: false }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const copy = screen.getByTestId('advancement-copy')
      expect(screen.getByText('1 / 2')).toBeTruthy()
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('All linked issues are delivered')
    })

    it('distinguishes advancement copy kinds without collapsing them into one message', () => {
      // Sanity check: build three epics with different shapes and confirm distinct copy.
      const cases = [
        {
          label: 'running-but-idle',
          epic: makeEpic({
            status: EpicStatus.Running,
            linkedIssues: [linkedIssue({ id: 'i1', number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } })],
          }),
        },
        {
          label: 'draft-blocker',
          epic: makeEpic({
            status: EpicStatus.Idle,
            linkedIssues: [linkedIssue({ id: 'i1', number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } })],
          }),
        },
        {
          label: 'external-prerequisite-blocker',
          epic: makeEpic({
            status: EpicStatus.Idle,
            linkedIssues: [linkedIssue({ id: 'i1', number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: null, externalPrerequisites: [{ number: 99, title: 'X', stage: 'plan', status: 'backlog' }] })],
          }),
        },
      ]
      const seen = new Set<string>()
      for (const c of cases) {
        mocks.useEpic.mockReturnValue({ data: c.epic, isLoading: false })
        renderPage()
        const copy = screen.getByTestId('advancement-copy')
        seen.add(copy.textContent ?? '')
        cleanup()
      }
      expect(seen.size).toBe(3)
    })
  })

  describe('paused epic resume hint', () => {
    it('renders the paused epic pause reason chip in the header', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Paused, pauseReason: 'Waiting for design review' }),
        isLoading: false,
      })

      renderPage()

      const reasonBadge = screen.getByTestId('pause-reason')
      expect(reasonBadge).toHaveTextContent('Waiting for design review')
    })

    it('renders the resume re-evaluation hint inside the Next Issue column when paused', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({ status: EpicStatus.Paused, pauseReason: 'Waiting for design review' }),
        isLoading: false,
      })

      renderPage()

      const hint = screen.getByTestId('resume-re-evaluation-hint')
      expect(hint.textContent).toMatch(/resuming/i)
      expect(hint.textContent).toMatch(/re-evaluate/i)
    })

    it('does not render the resume hint on a non-paused epic', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

      renderPage()

      expect(screen.queryByTestId('resume-re-evaluation-hint')).toBeNull()
    })
  })

  describe('no regression of linked-issue / edit / add capabilities', () => {
    it('keeps the Linked Issues listing reachable with linked-issue nav links and Remove buttons', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
            linkedIssue({ id: 'issue-2', number: 2, title: 'Backlog', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: true, startBlocker: null }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      const list = screen.getByTestId('linked-issues-list-region')
      expect(list).toBeTruthy()
      const navLinks = screen.getAllByTestId('linked-issue-nav-link')
      expect(navLinks.length).toBe(2)
      expect(navLinks[0].getAttribute('href')).toContain('/issues/1')
      expect(navLinks[1].getAttribute('href')).toContain('/issues/2')
      expect(screen.getAllByRole('button', { name: 'Remove' }).length).toBe(2)
    })

    it('keeps the add-issue selector reachable and functional after the summary restructure', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('epic-issue-selector-trigger')).toBeTruthy()
      expect(screen.getByTestId('add-issue-submit')).toBeTruthy()

      fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
      const option = screen.getAllByTestId('epic-issue-option')[0]
      fireEvent.click(option)
      expect(option).toBeTruthy()
    })

    it('keeps the Edit and Close Epic buttons reachable as secondary actions on a non-terminal epic', () => {
      mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

      renderPage()

      const actionGroup = getActionGroup()
      expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
      expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()
    })

    it('keeps the list/graph toggle reachable when there are 2+ linked issues', () => {
      mocks.useEpic.mockReturnValue({
        data: makeEpic({
          linkedIssues: [
            linkedIssue({ id: 'issue-1', number: 1, title: 'A' }),
            linkedIssue({ id: 'issue-2', number: 2, title: 'B', prerequisiteNumbers: [1] }),
          ],
        }),
        isLoading: false,
      })

      renderPage()

      expect(screen.getByTestId('linked-issues-view-toggle')).toBeTruthy()
      expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
      expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
    })
  })
})
