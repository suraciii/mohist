import { afterEach, describe, expect, it } from 'vitest'

import { cleanup, fireEvent, screen } from '@testing-library/react'

import { EpicStatus, type EpicDetail } from '../../../entities/epic'
import { IssueStatus, WorkflowStage, IssueHealth } from '../../../entities/issue'

import { issues, linkedIssue, renderPage, getActionGroup } from './_epicDetailPageTestUtils'
import { mountEpicDetail, mockEpic } from './_epicDetailMsw'
import { setScopedProperty } from '../../../../tests/support/scoped-property'

describe('EpicDetailPage summary-first information architecture', () => {
  const LONG_DESCRIPTION = [
    '## Background',
    '',
    'This is the long descriptive prose that previously appeared in the header card before the summary grid.',
    '',
    'It pushed the status facts below the first fold on narrow viewports.',
    '',
    Array.from({ length: 12 }, (_, i) => `Paragraph ${i + 1} with additional context and details.`).join('\n\n'),
  ].join('\n\n')

  function makeEpic(overrides: Record<string, unknown> = {}): EpicDetail {
    return {
      projectId: 'proj-1',
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
    } as EpicDetail
  }

  mountEpicDetail(makeEpic(), issues)

  afterEach(() => {
    mockEpic(makeEpic())
  })

  async function renderPageReady() {
    renderPage()
    await screen.findByTestId('epic-number')
  }

  function getSummaryGrid(): HTMLElement {
    const summary = screen.getByTestId('summary-grid')
    return summary
  }

  function getOverviewCard(): HTMLElement {
    return screen.getByTestId('overview-card')
  }

  describe('summary-before-description DOM order', () => {
    it('renders the summary grid before the Overview card on desktop', async () => {
      await renderPageReady()

      const summary = getSummaryGrid()
      const overview = getOverviewCard()
      expect(summary.compareDocumentPosition(overview) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('places the summary grid before the Overview card in DOM order on mobile (390px viewport)', async () => {
      await renderPageReady()

      const summary = getSummaryGrid()
      const overview = getOverviewCard()
      expect(summary.compareDocumentPosition(overview) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    })

    it('keeps the summary grid inside the header card while the Overview card sits below it', async () => {
      await renderPageReady()

      const summary = getSummaryGrid()
      const headerCard = summary.closest('[data-slot="card"]') as HTMLElement
      expect(headerCard).toBeTruthy()
      expect(headerCard.querySelector('[data-testid="overview-card"]')).toBeNull()
    })
  })

  describe('no Overview card when description is empty', () => {
    it('omits the Overview card entirely when epic.description is the empty string', async () => {
      mockEpic(makeEpic({ description: '' }))
      await renderPageReady()

      expect(screen.queryByTestId('overview-card')).toBeNull()
      expect(screen.queryByTestId('epic-description')).toBeNull()
    })

    it('still renders the summary grid when description is empty', async () => {
      mockEpic(makeEpic({ description: '' }))
      await renderPageReady()

      expect(screen.getByTestId('summary-grid')).toBeTruthy()
      expect(screen.getByText('1 / 3')).toBeTruthy()
    })
  })

  describe('Overview/Description region is collapsible via MarkdownReader', () => {
    it('renders the MarkdownReader in collapsible mode inside the Overview card', async () => {
      await renderPageReady()

      const description = screen.getByTestId('epic-description')
      const reader = description.querySelector('[data-testid="markdown-reader"]') as HTMLElement
      expect(reader).toBeTruthy()
      expect(reader.getAttribute('data-mode')).toBe('collapsible')
    })

    it('exposes the expand/collapse test hooks from MarkdownReader inside the Overview card', async () => {
      setScopedProperty(HTMLElement.prototype, 'scrollHeight', {
        configurable: true,
        get() {
          return 5000
        },
      })

      mockEpic(makeEpic({
        description: Array.from({ length: 80 }, (_, i) => `Line ${i + 1} content that exceeds the collapsed height.`).join('\n\n'),
      }))
      await renderPageReady()

      const description = screen.getByTestId('epic-description')
      const expandControl = description.querySelector('[data-testid="markdown-expand-control"]') as HTMLElement
      expect(expandControl).toBeTruthy()
    })
  })

  describe('progress summary', () => {
    it('shows delivered / total counts', async () => {
      mockEpic(makeEpic({ progress: { deliveredCount: 2, totalIssueCount: 5, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false } }))
      await renderPageReady()

      expect(screen.getByText('2 / 5')).toBeTruthy()
      expect(screen.queryByTestId('progress-ready-to-mark-done')).toBeNull()
    })

    it('surfaces a ready-to-mark-done indication when readyToMarkDone is true', async () => {
      mockEpic(makeEpic({ progress: { deliveredCount: 3, totalIssueCount: 3, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: true } }))
      await renderPageReady()

      expect(screen.getByText('3 / 3')).toBeTruthy()
      const indicator = screen.getByTestId('progress-ready-to-mark-done')
      expect(indicator).toBeTruthy()
      expect(indicator.textContent).toMatch(/ready to mark done/i)
    })

    it('omits the ready-to-mark-done indicator for terminal epics (done/closed)', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Done,
        progress: { deliveredCount: 1, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: true },
      }))
      await renderPageReady()

      expect(screen.queryByTestId('progress-ready-to-mark-done')).toBeNull()
    })
  })

  describe('advancement copy kinds', () => {
    it('renders waiting-for-in-progress copy with nav link to the in-progress issue', async () => {
      mockEpic(makeEpic({
        progress: { deliveredCount: 0, totalIssueCount: 2, blockedIssues: [], activeIssues: [{ number: 2, title: 'Active', health: 'active' }], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({ number: 1, title: 'Backlog', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: true, startBlocker: null }),
          linkedIssue({ number: 2, title: 'Active', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1', canStart: false }),
        ],
      }))
      await renderPageReady()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('Waiting for #2 to finish')
      const link = screen.getByTestId('advancement-link')
      expect(link.getAttribute('href')).toContain('/issues/2')
    })

    it('renders draft-blocker copy with nav link to the draft candidate', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({ number: 8, title: 'Draft candidate', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'draft' } }),
        ],
      }))
      await renderPageReady()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('still a draft')
      expect(copy.textContent).toContain('#8')
      const link = screen.getByTestId('advancement-link')
      expect(link.getAttribute('href')).toContain('/issues/8')
    })

    it('renders external-prerequisite-blocker copy with nav links to the prerequisites', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({
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
      }))
      await renderPageReady()

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

    it('renders running-but-idle copy without nav links for a running epic with no startable next', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Running,
        progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({ number: 1, title: 'Waiting on', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } }),
        ],
      }))
      await renderPageReady()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('Running')
      expect(copy.textContent).not.toContain('Idle')
      expect(screen.queryByTestId('advancement-link')).toBeNull()
    })

    it('renders has-next nav link without additional advancement copy when a server-provided next issue exists', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: { number: 3, title: 'Candidate' }, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({ number: 3, title: 'Candidate' }),
        ],
      }))
      await renderPageReady()

      const next = screen.getByTestId('next-issue')
      expect(next.getAttribute('href')).toContain('/issues/3')
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
    })

    it('does not render external-blocker copy below a startable next issue with prerequisite metadata', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: { number: 3, title: 'Candidate' }, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({
            number: 3,
            title: 'Candidate',
            status: IssueStatus.Backlog,
            stage: WorkflowStage.Plan,
            canStart: true,
            startBlocker: null,
            externalPrerequisites: [{ number: 77, title: 'Historical prerequisite', stage: 'done', status: 'done' }],
          }),
        ],
      }))
      await renderPageReady()

      const next = screen.getByTestId('next-issue')
      expect(next).toHaveTextContent('#3 Candidate')
      expect(next.getAttribute('href')).toContain('/issues/3')
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
      expect(screen.queryByText(/external issue/i)).toBeNull()
    })

    it('does not show a lower-priority draft blocker under a server-provided next issue', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 0, totalIssueCount: 2, blockedIssues: [], activeIssues: [], nextIssue: { number: 9, title: 'Priority candidate' }, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({ number: 4, title: 'Older draft', priority: 'p3', canStart: false, startBlocker: { kind: 'draft' } }),
          linkedIssue({ number: 9, title: 'Priority candidate', priority: 'p0', canStart: true, startBlocker: null }),
        ],
      }))
      await renderPageReady()

      const next = screen.getByTestId('next-issue')
      expect(next).toHaveTextContent('#9 Priority candidate')
      expect(next.getAttribute('href')).toContain('/issues/9')
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
      const advancementArea = screen.getByTestId('next-issue-region')
      expect(advancementArea.textContent ?? '').not.toMatch(/still a draft/i)
    })

    it('renders idle-no-next reason copy when an idle epic has no startable candidate and no specific blocker', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({ number: 1, title: 'Waiting on', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } }),
        ],
      }))
      await renderPageReady()

      const copy = screen.getByTestId('advancement-copy')
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('Running')
    })

    it('renders "No linked issues yet" when there are no linked issues and no next issue', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 0, totalIssueCount: 0, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [],
      }))
      await renderPageReady()

      expect(screen.getByText('No linked issues yet')).toBeTruthy()
      expect(screen.queryByTestId('advancement-copy')).toBeNull()
    })

    it('uses neutral copy for an all-cancelled epic instead of delivered wording', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 0, totalIssueCount: 1, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({ number: 7, title: 'Cancelled issue', status: IssueStatus.Cancelled, stage: WorkflowStage.Done, health: IssueHealth.Cancelled, canStart: false }),
        ],
      }))
      await renderPageReady()

      const copy = screen.getByTestId('advancement-copy')
      expect(screen.getByText('0 / 1')).toBeTruthy()
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('All linked issues are delivered')
    })

    it('uses neutral copy for mixed done and cancelled issues instead of delivered wording', async () => {
      mockEpic(makeEpic({
        status: EpicStatus.Idle,
        progress: { deliveredCount: 1, totalIssueCount: 2, blockedIssues: [], activeIssues: [], nextIssue: null, nextIssueReason: null, readyToMarkDone: false },
        linkedIssues: [
          linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, canStart: false }),
          linkedIssue({ number: 7, title: 'Cancelled issue', status: IssueStatus.Cancelled, stage: WorkflowStage.Done, health: IssueHealth.Cancelled, canStart: false }),
        ],
      }))
      await renderPageReady()

      const copy = screen.getByTestId('advancement-copy')
      expect(screen.getByText('1 / 2')).toBeTruthy()
      expect(copy.textContent).toContain('No startable next issue')
      expect(copy.textContent).not.toContain('All linked issues are delivered')
    })

    it('distinguishes advancement copy kinds without collapsing them into one message', async () => {
      const cases = [
        {
          label: 'running-but-idle',
          epic: makeEpic({
            status: EpicStatus.Running,
            linkedIssues: [linkedIssue({ number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'waiting-for', issue: { number: 99, title: 'X' } } })],
          }),
        },
        {
          label: 'draft-blocker',
          epic: makeEpic({
            status: EpicStatus.Idle,
            linkedIssues: [linkedIssue({ number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: { kind: 'draft' } })],
          }),
        },
        {
          label: 'external-prerequisite-blocker',
          epic: makeEpic({
            status: EpicStatus.Idle,
            linkedIssues: [linkedIssue({ number: 1, status: IssueStatus.Backlog, canStart: false, startBlocker: null, externalPrerequisites: [{ number: 99, title: 'X', stage: 'plan', status: 'backlog' }] })],
          }),
        },
      ]
      const seen = new Set<string>()
      for (const c of cases) {
        mockEpic(c.epic)
        renderPage()
        const copy = await screen.findByTestId('advancement-copy')
        seen.add(copy.textContent ?? '')
        cleanup()
      }
      expect(seen.size).toBe(3)
    })
  })

  describe('paused epic resume hint', () => {
    it('renders the paused epic pause reason chip in the header', async () => {
      mockEpic(makeEpic({ status: EpicStatus.Paused, pauseReason: 'Waiting for design review' }))
      await renderPageReady()

      const reasonBadge = screen.getByTestId('pause-reason')
      expect(reasonBadge).toHaveTextContent('Waiting for design review')
    })

    it('renders the resume re-evaluation hint inside the Next Issue column when paused', async () => {
      mockEpic(makeEpic({ status: EpicStatus.Paused, pauseReason: 'Waiting for design review' }))
      await renderPageReady()

      const hint = screen.getByTestId('resume-re-evaluation-hint')
      expect(hint.textContent).toMatch(/resuming/i)
      expect(hint.textContent).toMatch(/re-evaluate/i)
    })

    it('does not render the resume hint on a non-paused epic', async () => {
      mockEpic(makeEpic({ status: EpicStatus.Running }))
      await renderPageReady()

      expect(screen.queryByTestId('resume-re-evaluation-hint')).toBeNull()
    })
  })

  describe('no regression of linked-issue / edit / add capabilities', () => {
    it('keeps the Linked Issues listing reachable with linked-issue nav links and Remove buttons', async () => {
      mockEpic(makeEpic({
        linkedIssues: [
          linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
          linkedIssue({ number: 2, title: 'Backlog', status: IssueStatus.Backlog, stage: WorkflowStage.Plan, canStart: true, startBlocker: null }),
        ],
      }))
      await renderPageReady()

      const list = screen.getByTestId('linked-issues-list-region')
      expect(list).toBeTruthy()
      const navLinks = screen.getAllByTestId('linked-issue-nav-link')
      expect(navLinks.length).toBe(2)
      expect(navLinks[0].getAttribute('href')).toContain('/issues/1')
      expect(navLinks[1].getAttribute('href')).toContain('/issues/2')
      expect(screen.getAllByRole('button', { name: 'Remove' }).length).toBe(2)
    })

    it('keeps the add-issue selector reachable and functional after the summary restructure', async () => {
      mockEpic(makeEpic({
        linkedIssues: [
          linkedIssue({ number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
        ],
      }))
      await renderPageReady()

      expect(screen.getByTestId('epic-issue-selector-trigger')).toBeTruthy()
      expect(screen.getByTestId('add-issue-submit')).toBeTruthy()

      fireEvent.click(screen.getByTestId('epic-issue-selector-trigger'))
      const option = screen.getAllByTestId('epic-issue-option')[0]
      fireEvent.click(option)
      expect(option).toBeTruthy()
    })

    it('keeps the Edit and Close Epic buttons reachable as secondary actions on a non-terminal epic', async () => {
      mockEpic(makeEpic({ status: EpicStatus.Running }))
      await renderPageReady()

      const actionGroup = getActionGroup()
      expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
      expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()
    })

    it('keeps the list/graph toggle reachable when there are 2+ linked issues', async () => {
      mockEpic(makeEpic({
        linkedIssues: [
          linkedIssue({ number: 1, title: 'A' }),
          linkedIssue({ number: 2, title: 'B', prerequisiteNumbers: [1] }),
        ],
      }))
      await renderPageReady()

      expect(screen.getByTestId('linked-issues-view-toggle')).toBeTruthy()
      expect(screen.getByTestId('linked-issues-view-list')).toHaveAttribute('aria-selected', 'true')
      expect(screen.getByTestId('linked-issues-view-graph')).toHaveAttribute('aria-selected', 'false')
    })
  })
})
