// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { IssuePrerequisitePicker } from './IssuePrerequisitePicker'

const PROJECT_ID = 'proj_test_001'

function buildIssue(overrides: Partial<Issue> & Pick<Issue, 'number' | 'title'>): Issue {
  return {
    id: `issue_${overrides.number}`,
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
]

const mocks = vi.hoisted(() => ({
  useIssues: vi.fn(),
}))

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssues: mocks.useIssues,
  }
})

function setIssues(issues: Issue[] | undefined, isLoading = false) {
  mocks.useIssues.mockReturnValue({
    data: issues,
    isLoading,
  })
}

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
    cleanup()
    vi.clearAllMocks()
  })

  describe('candidate search', () => {
    it('surfaces a matching issue when the user types its number', async () => {
      setIssues(TEST_ISSUES)
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
      setIssues(TEST_ISSUES)
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'AUTH' } })

      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers.sort()).toEqual([10, 42, 50])
    })

    it('surfaces issues whose raw status matches the typed term', async () => {
      setIssues(TEST_ISSUES)
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'in_progress' } })

      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers.sort()).toEqual([10, 50])
    })

    it('surfaces issues whose rendered status label matches the typed term', async () => {
      setIssues(TEST_ISSUES)
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'in progress' } })

      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers.sort()).toEqual([10, 50])
    })

    it('surfaces backlog and done issues by status terms', async () => {
      setIssues(TEST_ISSUES)
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'backlog' } })
      let options = await screen.findAllByTestId('prerequisite-picker-option')
      expect(options.map((opt) => Number(opt.getAttribute('data-issue-number')))).toEqual([42])

      fireEvent.change(search, { target: { value: 'done' } })
      options = await screen.findAllByTestId('prerequisite-picker-option')
      expect(options.map((opt) => Number(opt.getAttribute('data-issue-number')))).toEqual([70])
    })

    it('shows no-match when the search term matches nothing', async () => {
      setIssues(TEST_ISSUES)
      renderPicker()

      openPicker()
      const search = await screen.findByTestId('prerequisite-picker-search')
      fireEvent.change(search, { target: { value: 'zzz-no-match' } })

      expect(screen.getByText(/No issues match/)).toBeInTheDocument()
    })

    it('renders each candidate with its number, title, status badge, and project/repository context', async () => {
      setIssues(TEST_ISSUES)
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
      setIssues(TEST_ISSUES)
      renderPicker({ excludeNumbers: [10] })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      expect(numbers).not.toContain(10)
    })

    it('does not offer already-selected prerequisites as candidates', async () => {
      setIssues(TEST_ISSUES)
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
      mocks.useIssues.mockImplementation((params: { projectId?: string } | undefined) => ({
        data: params?.projectId === PROJECT_ID ? projectAIssues : projectBIssues,
      }))

      renderPicker({ projectId: 'proj_other', excludeNumbers: [] })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((opt) => Number(opt.getAttribute('data-issue-number')))
      for (const issue of projectAIssues) {
        expect(numbers).not.toContain(issue.number)
      }
      expect(numbers).toEqual([99])
      expect(mocks.useIssues).toHaveBeenCalledWith({ projectId: 'proj_other' })
    })
  })

  describe('chips', () => {
    it('renders selected numbers as removable chips beneath the picker', () => {
      setIssues(TEST_ISSUES)
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
      setIssues(TEST_ISSUES)
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
      setIssues(TEST_ISSUES)
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
      setIssues(TEST_ISSUES)
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
  })

  describe('incomplete / start-eligibility messaging', () => {
    it('flags an incomplete prerequisite (completed === false) with an indicator', () => {
      setIssues(TEST_ISSUES)
      renderPicker({ selected: [10, 70], excludeNumbers: [10, 70] })

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
      setIssues(TEST_ISSUES.filter((issue) => issue.number !== 99))
      renderPicker({
        selected: [99],
        excludeNumbers: [99],
        selectedIssueSummaries: [
          {
            issueId: 'issue_99',
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
      setIssues(TEST_ISSUES)
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
      setIssues(TEST_ISSUES)
      renderPicker({ selected: [], canStart: true, blocker: null })

      const readiness = screen.getByTestId('prerequisite-picker-readiness')
      expect(readiness).toHaveAttribute('data-can-start', 'true')
      expect(readiness).toHaveTextContent('Ready to start')
    })

    it('does not render a readiness line when canStart and blocker are not supplied', () => {
      setIssues(TEST_ISSUES)
      renderPicker({ selected: [] })

      expect(screen.queryByTestId('prerequisite-picker-readiness')).not.toBeInTheDocument()
    })
  })

  describe('mode seam', () => {
    it('marks the chips container with the current mode for downstream selectors', () => {
      setIssues(TEST_ISSUES)
      renderPicker({ mode: 'buffer', selected: [42], excludeNumbers: [42] })
      expect(screen.getByTestId('prerequisite-picker-chips')).toHaveAttribute('data-mode', 'buffer')

      cleanup()
      renderPicker({ mode: 'live', selected: [42], excludeNumbers: [42] })
      expect(screen.getByTestId('prerequisite-picker-chips')).toHaveAttribute('data-mode', 'live')
    })
  })
})

function within(el: HTMLElement) {
  // @testing-library/dom's `within` is not imported by default in this file; replicate the
  // minimal API the tests need so they read identically to a real RTL test.
  return {
    getByTestId: (testId: string) => {
      const found = el.querySelector(`[data-testid="${testId}"]`)
      if (!found) throw new Error(`[data-testid="${testId}"] not found within ${el.outerHTML}`)
      return found as HTMLElement
    },
    queryByTestId: (testId: string) => el.querySelector(`[data-testid="${testId}"]`) as HTMLElement | null,
  }
}
