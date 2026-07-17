import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'

import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { IssuePrerequisitePicker } from './IssuePrerequisitePicker'
import { useMswServer } from '../../../../tests/support/msw'

const PROJECT_ID = 'proj_test_001'

function buildIssue(overrides: Partial<Issue> & Pick<Issue, 'number' | 'title'>): Issue {
  return {
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: PROJECT_ID,
    projectName: 'Mohist',
    repository: { name: 'main', gitUrl: 'file://main', baseBranch: 'main' },
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
    number: overrides.number,
    title: overrides.title,
  } as Issue
}

const TEST_ISSUES: Issue[] = [
  buildIssue({ number: 10, title: 'Wire up auth', status: IssueStatus.InProgress, health: IssueHealth.Active }),
  buildIssue({ number: 42, title: 'Audit auth tokens', status: IssueStatus.Backlog, health: IssueHealth.Active }),
  buildIssue({ number: 50, title: 'Fix auth timeout', status: IssueStatus.InProgress, health: IssueHealth.Active }),
  buildIssue({ number: 70, title: 'Ship refactor', status: IssueStatus.Done, health: IssueHealth.Done }),
  buildIssue({ number: 88, title: 'Archived completed dependency', status: IssueStatus.Done, health: IssueHealth.Done, archivedAt: '2026-01-02T00:00:00Z' }),
]

let _issuesData: Issue[] = []

useMswServer(
  http.get('*/api/projects/:projectId/issues', () => {
    return HttpResponse.json({ success: true, data: _issuesData })
  }),
)

function renderPicker(props: Partial<React.ComponentProps<typeof IssuePrerequisitePicker>> = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <IssuePrerequisitePicker
        projectId={PROJECT_ID}
        excludeNumbers={[]}
        selected={[]}
        mode="buffer"
        onAdd={vi.fn()}
        onRemove={vi.fn()}
        {...props}
      />
    </QueryClientProvider>,
  )
  return { queryClient, ...view }
}

function openPicker() {
  fireEvent.click(screen.getByTestId('prerequisite-picker-trigger'))
}

describe('IssuePrerequisitePicker', () => {
  afterEach(() => {
    _issuesData = []
    vi.clearAllMocks()
  })

  describe('candidate search', () => {
    it('surfaces a matching issue when the user types its number', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: '42' } })

      const options = await screen.findAllByTestId('prerequisite-picker-option')
      expect(options).toHaveLength(1)
      expect(options[0]).toHaveAttribute('data-issue-number', '42')
      expect(options[0]).toHaveTextContent('#42')
      expect(options[0]).toHaveTextContent('Audit auth tokens')
      expect(options[0]).toHaveTextContent('Backlog')
    })

    it('surfaces all issues whose titles case-insensitively contain the typed fragment', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'AUTH' } })

      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers.sort()).toEqual([10, 42, 50])
    })

    it('surfaces issues whose raw status matches the typed term', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'in_progress' } })

      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers.sort()).toEqual([10, 50])
    })

    it('surfaces issues whose rendered status label matches the typed term', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'in progress' } })

      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers.sort()).toEqual([10, 50])
    })

    it('surfaces backlog and done issues by status terms', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'backlog' } })
      let options = await screen.findAllByTestId('prerequisite-picker-option')
      expect(options.map((opt) => Number(opt.getAttribute('data-issue-number')))).toEqual([42])

      fireEvent.change(search, { target: { value: 'done' } })
      options = await screen.findAllByTestId('prerequisite-picker-option')
      expect(options.map((opt) => Number(opt.getAttribute('data-issue-number')))).toEqual([70, 88])
    })

    it('requests all project issues so archived completed issues remain valid choices', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))

      expect(numbers).toContain(88)
      expect(options.find((opt) => opt.getAttribute('data-issue-number') === '88')).toHaveTextContent('Archived completed dependency')
    })

    it('shows no-match when the search term matches nothing', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'zzz-no-match' } })

      expect(screen.getByText(/No issues match/)).toBeInTheDocument()
    })

    it('renders each candidate with its number, title, status badge, and project/repository context', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const option10 = options.find(
        (opt) => opt.getAttribute('data-issue-number') === '10',
      )
      expect(option10).toBeDefined()
      expect(option10!).toHaveTextContent('#10')
      expect(option10!).toHaveTextContent('Wire up auth')
      expect(option10!).toHaveTextContent('In Progress')
      expect(option10!).toHaveTextContent('Mohist / main')
      expect(option10!.querySelector('[data-testid="prerequisite-picker-option-badge"]')).toBeTruthy()
    })
  })

  describe('exclusions', () => {
    it('does not offer the current issue (in excludeNumbers) as a candidate', async () => {
      _issuesData = TEST_ISSUES
      renderPicker({ excludeNumbers: [10] })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers).not.toContain(10)
    })

    it('does not offer already-selected prerequisites as candidates', async () => {
      _issuesData = TEST_ISSUES
      renderPicker({ selected: [42], excludeNumbers: [42] })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers).not.toContain(42)
    })

    it('does not offer cross-project issues because the candidate query is project-scoped', async () => {
      const projectAIssues = TEST_ISSUES
      const projectBIssues: Issue[] = [
        buildIssue({ number: 99, title: 'Only in project B', status: IssueStatus.Backlog, health: IssueHealth.Active }),
      ]
      _issuesData = projectBIssues

      renderPicker({ projectId: 'proj_other', excludeNumbers: [] })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      for (const issue of projectAIssues) {
        expect(numbers).not.toContain(issue.number)
      }
      expect(numbers).toEqual([99])
    })
  })

  describe('popover sizing', () => {
    it('uses the Base UI anchor width variable for trigger-aligned popup width', async () => {
      _issuesData = TEST_ISSUES
      renderPicker()

      openPicker()
      const listbox = await screen.findByTestId('prerequisite-picker-listbox')
      const content = listbox.closest('[data-slot="popover-content"]')

      expect(content).toHaveClass('w-[var(--anchor-width)]')
    })
  })

  describe('chips', () => {
    it('renders selected numbers as removable chips beneath the picker', () => {
      _issuesData = TEST_ISSUES
      renderPicker({ selected: [42, 50], excludeNumbers: [42, 50] })

      const chips = screen.getAllByTestId('prerequisite-picker-chip')
      expect(chips).toHaveLength(2)
      expect(chips[0]).toHaveAttribute('data-issue-number', '42')
      expect(chips[1]).toHaveAttribute('data-issue-number', '50')
      for (const chip of chips) {
        expect(within(chip).getByTestId('prerequisite-picker-chip-remove')).toBeInTheDocument()
      }
    })

    it('removes a chip in mode="buffer" by invoking onRemove (parent updates local state)', async () => {
      _issuesData = TEST_ISSUES
      const onRemove = vi.fn()
      const onAdd = vi.fn()
      const user = userEvent.setup()
      const { rerender, queryClient } = renderPicker({
        mode: 'buffer',
        selected: [42, 50],
        excludeNumbers: [42, 50],
        onAdd,
        onRemove,
      })

      const chips = screen.getAllByTestId('prerequisite-picker-chip')
      await user.click(within(chips[0]).getByTestId('prerequisite-picker-chip-remove'))

      expect(onRemove).toHaveBeenCalledWith(42)

      rerender(
        <QueryClientProvider client={queryClient}>
          <IssuePrerequisitePicker
            projectId={PROJECT_ID}
            excludeNumbers={[50]}
            selected={[50]}
            mode="buffer"
            onAdd={onAdd}
            onRemove={onRemove}
          />
        </QueryClientProvider>,
      )

      const remaining = screen.getAllByTestId('prerequisite-picker-chip')
      expect(remaining).toHaveLength(1)
      expect(remaining[0]).toHaveAttribute('data-issue-number', '50')
    })

    it('removes a chip in mode="live" by invoking onRemove (parent triggers removePrerequisite mutation)', async () => {
      _issuesData = TEST_ISSUES
      const onRemove = vi.fn().mockResolvedValue(undefined)
      const onAdd = vi.fn()
      const user = userEvent.setup()
      renderPicker({
        mode: 'live',
        selected: [42],
        excludeNumbers: [42],
        onAdd,
        onRemove,
      })

      const chip = screen.getByTestId('prerequisite-picker-chip')
      await user.click(within(chip).getByTestId('prerequisite-picker-chip-remove'))

      expect(onRemove).toHaveBeenCalledWith(42)
    })

    it('selects a candidate in mode="live" by invoking onAdd (parent triggers addPrerequisite mutation)', async () => {
      _issuesData = TEST_ISSUES
      const onAdd = vi.fn().mockResolvedValue(undefined)
      const onRemove = vi.fn()
      renderPicker({ mode: 'live', selected: [], excludeNumbers: [], onAdd, onRemove })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const option42 = options.find(
        (opt) => opt.getAttribute('data-issue-number') === '42',
      )
      expect(option42).toBeDefined()
      fireEvent.click(option42!)

      await waitFor(() => expect(onAdd).toHaveBeenCalledWith(42))
    })

    it('ignores duplicate live-mode candidate clicks while add is pending', async () => {
      _issuesData = TEST_ISSUES
      let resolveAdd!: () => void
      const onAdd = vi.fn(() => new Promise<void>((resolve) => { resolveAdd = resolve }))
      const onRemove = vi.fn()
      renderPicker({ mode: 'live', selected: [], excludeNumbers: [], onAdd, onRemove })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const option42 = options.find(
        (opt) => opt.getAttribute('data-issue-number') === '42',
      )
      expect(option42).toBeDefined()
      fireEvent.click(option42!)
      fireEvent.click(option42!)

      expect(onAdd).toHaveBeenCalledTimes(1)
      expect(onAdd).toHaveBeenCalledWith(42)
      resolveAdd()
      await waitFor(() => expect(option42!).not.toBeDisabled())
    })

    it('ignores duplicate live-mode chip removals while remove is pending', async () => {
      _issuesData = TEST_ISSUES
      let resolveRemove!: () => void
      const onRemove = vi.fn(() => new Promise<void>((resolve) => { resolveRemove = resolve }))
      const onAdd = vi.fn()
      renderPicker({
        mode: 'live',
        selected: [42],
        excludeNumbers: [42],
        onAdd,
        onRemove,
      })

      const chip = screen.getByTestId('prerequisite-picker-chip')
      const remove = within(chip).getByTestId('prerequisite-picker-chip-remove')
      fireEvent.click(remove)
      fireEvent.click(remove)

      expect(onRemove).toHaveBeenCalledTimes(1)
      expect(onRemove).toHaveBeenCalledWith(42)
      resolveRemove()
      await waitFor(() => expect(remove).not.toBeDisabled())
    })
  })

  describe('incomplete / start-eligibility messaging', () => {
    it('flags an incomplete prerequisite (completed === false) with an indicator', async () => {
      _issuesData = TEST_ISSUES
      renderPicker({ selected: [10, 70], excludeNumbers: [10, 70] })

      await screen.findByText(/Wire up auth/)
      const chips = screen.getAllByTestId('prerequisite-picker-chip')
      const incompleteChip = chips.find((chip) => chip.getAttribute('data-issue-number') === '10')
      const completedChip = chips.find((chip) => chip.getAttribute('data-issue-number') === '70')

      expect(incompleteChip).toBeDefined()
      expect(completedChip).toBeDefined()
      expect(incompleteChip!).toHaveAttribute('data-incomplete', 'true')
      expect(
        within(incompleteChip!).getByTestId('prerequisite-picker-chip-incomplete-indicator'),
      ).toBeInTheDocument()

      expect(completedChip!).toHaveAttribute('data-incomplete', 'false')
      expect(
        within(completedChip!).queryByTestId('prerequisite-picker-chip-incomplete-indicator'),
      ).not.toBeInTheDocument()
    })

    it('prefers selected prerequisite summaries when a selected issue is missing from the picker list', () => {
      _issuesData = TEST_ISSUES.filter((issue) => issue.number !== 99)
      renderPicker({
        selected: [99],
        excludeNumbers: [99],
        selectedIssueSummaries: [
          {
            number: 99,
            title: 'Authoritative prerequisite',
            completed: false,
            status: IssueStatus.Backlog,
            health: IssueHealth.Active,
          },
        ],
      })

      const chip = screen.getByTestId('prerequisite-picker-chip')
      expect(chip).toHaveTextContent('#99 · Authoritative prerequisite')
      expect(chip).toHaveAttribute('data-incomplete', 'true')
      expect(within(chip).getByTestId('prerequisite-picker-chip-incomplete-indicator')).toBeInTheDocument()
    })

    it('renders a canStart / blocker summary line when those fields are supplied', () => {
      _issuesData = TEST_ISSUES
      renderPicker({
        selected: [10],
        excludeNumbers: [10],
        canStart: false,
        blocker: { kind: 'waiting-for', issue: { number: 10, title: 'Wire up auth' } },
      })

      const readiness = screen.getByTestId('prerequisite-picker-readiness')
      expect(readiness).toHaveAttribute('data-can-start', 'false')
      expect(readiness).toHaveTextContent('Cannot start: waiting on #10')
    })

    it('renders a ready line when canStart is true and blocker is null', () => {
      _issuesData = TEST_ISSUES
      renderPicker({ selected: [], canStart: true, blocker: null })

      const readiness = screen.getByTestId('prerequisite-picker-readiness')
      expect(readiness).toHaveAttribute('data-can-start', 'true')
      expect(readiness).toHaveTextContent('Ready to start')
    })

    it('does not render a readiness line when canStart and blocker are not supplied', () => {
      _issuesData = TEST_ISSUES
      renderPicker({ selected: [] })

      expect(screen.queryByTestId('prerequisite-picker-readiness')).not.toBeInTheDocument()
    })
  })

  describe('mode seam', () => {
    it('marks the chips container with the current mode for downstream selectors', () => {
      _issuesData = TEST_ISSUES
      renderPicker({ mode: 'buffer', selected: [42], excludeNumbers: [42] })
      expect(screen.getByTestId('prerequisite-picker-chips')).toHaveAttribute('data-mode', 'buffer')

      cleanup()
      renderPicker({ mode: 'live', selected: [42], excludeNumbers: [42] })
      expect(screen.getByTestId('prerequisite-picker-chips')).toHaveAttribute('data-mode', 'live')
    })
  })
})

function within(el: HTMLElement) {
  return {
    getByTestId: (testId: string) => {
      const found = el.querySelector(`[data-testid="${testId}"]`)
      if (!found) throw new Error(`[data-testid="${testId}"] not found within ${el.outerHTML}`)
      return found as HTMLElement
    },
    queryByTestId: (testId: string) => el.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null,
  }
}
