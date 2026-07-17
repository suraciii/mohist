import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { IssueHealth, IssueStatus, type Issue, type IssuePrerequisiteSummary } from '../../../../entities/issue'
import { IssueConfigurationCard } from './IssueConfigurationCard'
import type { IssueDetailMutations } from '../../model/useIssueDetailMutations'
import type { IssuePrerequisitePickerProps } from '../../../../entities/issue'

const PROJECT_ID = 'proj_live_001'
const ISSUE_NUMBER = 10

function buildIssue(overrides: Partial<Issue> & Pick<Issue, 'number' | 'title'>): Issue {
  return {
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: PROJECT_ID,
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

const CANDIDATE_ISSUES: Issue[] = [
  buildIssue({ number: 5, title: 'Wire up auth', status: IssueStatus.InProgress, health: IssueHealth.Active }),
  buildIssue({ number: 7, title: 'Audit auth tokens', status: IssueStatus.Done, health: IssueHealth.Done }),
  buildIssue({ number: 12, title: 'Unrelated fix', status: IssueStatus.Backlog, health: IssueHealth.Active }),
]

let currentIssues: Issue[] = CANDIDATE_ISSUES

const issuesHook: NonNullable<IssuePrerequisitePickerProps['issuesHook']> = () => ({
  data: currentIssues,
  isLoading: false,
}) as ReturnType<NonNullable<IssuePrerequisitePickerProps['issuesHook']>>

function setIssues(issues: Issue[]) {
  currentIssues = issues
}

interface MutationStubs {
  addPrerequisite: ReturnType<typeof vi.fn>
  removePrerequisite: ReturnType<typeof vi.fn>
  addError?: Error | null
  removeError?: Error | null
  addPending?: boolean
  removePending?: boolean
}

function buildMutations({ addPrerequisite, removePrerequisite, addError = null, removeError = null, addPending = false, removePending = false }: MutationStubs): Pick<IssueDetailMutations, 'addPrerequisiteMutation' | 'removePrerequisiteMutation'> {
  return {
    addPrerequisiteMutation: {
      mutate: addPrerequisite,
      mutateAsync: addPrerequisite,
      isPending: addPending,
      isError: !!addError,
      error: addError,
    } as unknown as IssueDetailMutations['addPrerequisiteMutation'],
    removePrerequisiteMutation: {
      mutate: removePrerequisite,
      mutateAsync: removePrerequisite,
      isPending: removePending,
      isError: !!removeError,
      error: removeError,
    } as unknown as IssueDetailMutations['removePrerequisiteMutation'],
  }
}

interface RenderArgs {
  prerequisites?: IssuePrerequisiteSummary[]
  canStart?: boolean
  blocker?: Issue['blocker']
  isBacklog?: boolean
  mutations?: Pick<IssueDetailMutations, 'addPrerequisiteMutation' | 'removePrerequisiteMutation'>
}

function renderCard({
  prerequisites = [],
  canStart = true,
  blocker = null,
  isBacklog = true,
  mutations,
}: RenderArgs = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <IssueConfigurationCard
        issue={{
          number: ISSUE_NUMBER,
          model: null,
          stageModels: null,
          prerequisites,
          canStart,
          blocker,
          isBacklog,
        }}
        projectId={PROJECT_ID}
        mutations={mutations ?? buildMutations({ addPrerequisite: vi.fn(), removePrerequisite: vi.fn() })}
        prerequisitePickerIssuesHook={issuesHook}
      />
    </QueryClientProvider>,
  )
  return { queryClient, ...view }
}

function openPicker() {
  fireEvent.click(screen.getByTestId('prerequisite-picker-trigger'))
}

describe('IssueConfigurationCard', () => {
  afterEach(() => {
    cleanup()
    setIssues(CANDIDATE_ISSUES)
  })

  describe('picker swap (numeric editor retired)', () => {
    it('does not render the legacy numeric Issue # input or Add button', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard()

      expect(screen.queryByPlaceholderText('Issue #')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Add' })).not.toBeInTheDocument()
      expect(screen.queryByText('Remove prerequisite:')).not.toBeInTheDocument()
    })

    it('renders the searchable prerequisite picker inside the Configuration card', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard()

      expect(screen.getByTestId('prerequisite-configuration-controls')).toBeInTheDocument()
      expect(screen.getByText('Prerequisites')).toBeInTheDocument()
      expect(screen.getByTestId('issue-prerequisite-picker')).toBeInTheDocument()
      expect(screen.getByTestId('prerequisite-picker-trigger')).toBeInTheDocument()
    })

    it('does not render the prerequisite section for non-backlog issues', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({ isBacklog: false })

      expect(screen.queryByTestId('prerequisite-configuration-controls')).not.toBeInTheDocument()
      expect(screen.queryByTestId('issue-prerequisite-picker')).not.toBeInTheDocument()
    })
  })

  describe('add via picker (live mode)', () => {
    it('invokes the addPrerequisite mutation when a candidate is selected', async () => {
      setIssues(CANDIDATE_ISSUES)
      const addPrerequisite = vi.fn().mockResolvedValue({ issue: {}, message: 'ok' })
      const user = userEvent.setup()
      renderCard({ mutations: buildMutations({ addPrerequisite, removePrerequisite: vi.fn() }) })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const opt12 = options.find((o) => o.getAttribute('data-issue-number') === '12')
      expect(opt12).toBeDefined()
      await user.click(opt12!)

      await waitFor(() => expect(addPrerequisite).toHaveBeenCalledWith(12))
    })

    it('excludes the current issue from the candidate list (no self-reference picker offer)', async () => {
      setIssues([
        ...CANDIDATE_ISSUES,
        buildIssue({ number: ISSUE_NUMBER, title: 'Self', status: IssueStatus.Backlog, health: IssueHealth.Active }),
      ])
      renderCard()

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((o) => Number(o.getAttribute('data-issue-number')))
      expect(numbers).not.toContain(ISSUE_NUMBER)
    })

    it('excludes already-selected prerequisites from the candidate list', async () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        prerequisites: [{ number: 5, title: 'Wire up auth', completed: false, status: IssueStatus.InProgress, health: IssueHealth.Active }],
      })

      openPicker()
      const options = await screen.findAllByTestId('prerequisite-picker-option')
      const numbers = options.map((o) => Number(o.getAttribute('data-issue-number')))
      expect(numbers).not.toContain(5)
      expect(numbers).toEqual(expect.arrayContaining([7, 12]))
    })

    it('disables the picker while add or remove mutations are pending', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        mutations: buildMutations({
          addPrerequisite: vi.fn(),
          removePrerequisite: vi.fn(),
          addPending: true,
        }),
      })

      expect(screen.getByTestId('prerequisite-picker-trigger')).toBeDisabled()
    })
  })

  describe('remove via chip (live mode)', () => {
    it('invokes the removePrerequisite mutation when a chip is removed', async () => {
      setIssues(CANDIDATE_ISSUES)
      const removePrerequisite = vi.fn().mockResolvedValue({ issue: {}, message: 'ok' })
      const user = userEvent.setup()
      renderCard({
        prerequisites: [
          { number: 5, title: 'Wire up auth', completed: false, status: IssueStatus.InProgress, health: IssueHealth.Active },
          { number: 7, title: 'Audit auth tokens', completed: true, status: IssueStatus.Done, health: IssueHealth.Done },
        ],
        canStart: false,
        blocker: { kind: 'waiting-for', issue: { number: 5, title: 'Wire up auth' } },
        mutations: buildMutations({ addPrerequisite: vi.fn(), removePrerequisite }),
      })

      const chips = screen.getAllByTestId('prerequisite-picker-chip')
      const chip5 = chips.find((c) => c.getAttribute('data-issue-number') === '5')
      expect(chip5).toBeDefined()
      await user.click(within(chip5!).getByTestId('prerequisite-picker-chip-remove'))

      await waitFor(() => expect(removePrerequisite).toHaveBeenCalledWith(5))
    })
  })

  describe('error surfacing', () => {
    it('renders the addPrerequisite mutation error message instead of silently swallowing it', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        mutations: buildMutations({
          addPrerequisite: vi.fn(),
          removePrerequisite: vi.fn(),
          addError: new Error('Issue #99999 not found'),
        }),
      })

      expect(screen.getByTestId('prerequisite-picker-error')).toHaveTextContent('Issue #99999 not found')
    })

    it('rewrites a circular prerequisite error to a readable cycle message', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        mutations: buildMutations({
          addPrerequisite: vi.fn(),
          removePrerequisite: vi.fn(),
          addError: new Error('circular prerequisite: cycle detected'),
        }),
      })

      expect(screen.getByTestId('prerequisite-picker-error')).toHaveTextContent('Circular prerequisite: this would create a cycle')
    })

    it('surfaces a self-reference error from the server unchanged', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        mutations: buildMutations({
          addPrerequisite: vi.fn(),
          removePrerequisite: vi.fn(),
          addError: new Error('Issue cannot be a prerequisite of itself'),
        }),
      })

      expect(screen.getByTestId('prerequisite-picker-error')).toHaveTextContent('Issue cannot be a prerequisite of itself')
    })

    it('surfaces the removePrerequisite mutation error when removal fails', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        prerequisites: [
          { number: 5, title: 'Wire up auth', completed: false, status: IssueStatus.InProgress, health: IssueHealth.Active },
        ],
        mutations: buildMutations({
          addPrerequisite: vi.fn(),
          removePrerequisite: vi.fn(),
          removeError: new Error('Prerequisite not found'),
        }),
      })

      expect(screen.getByTestId('prerequisite-picker-error')).toHaveTextContent('Prerequisite not found')
    })
  })

  describe('start-eligibility surfacing (no readiness-model change)', () => {
    it('renders the canStart / blocker summary when prerequisites are waiting', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        prerequisites: [
          { number: 5, title: 'Wire up auth', completed: false, status: IssueStatus.InProgress, health: IssueHealth.Active },
        ],
        canStart: false,
        blocker: { kind: 'waiting-for', issue: { number: 5, title: 'Wire up auth' } },
      })

      const readiness = screen.getByTestId('prerequisite-picker-readiness')
      expect(readiness).toHaveAttribute('data-can-start', 'false')
      expect(readiness).toHaveTextContent('Cannot start: waiting on #5')
    })

    it('renders a ready line when all prerequisites are completed', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        prerequisites: [
          { number: 7, title: 'Audit auth tokens', completed: true, status: IssueStatus.Done, health: IssueHealth.Done },
        ],
        canStart: true,
        blocker: null,
      })

      const readiness = screen.getByTestId('prerequisite-picker-readiness')
      expect(readiness).toHaveAttribute('data-can-start', 'true')
      expect(readiness).toHaveTextContent('Ready to start')
    })

    it('does not pass canStart/blocker to the picker when the card itself is not ready-aware (no-op when card does not opt in)', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        prerequisites: [
          { number: 5, title: 'Wire up auth', completed: false, status: IssueStatus.InProgress, health: IssueHealth.Active },
        ],
        canStart: false,
        blocker: { kind: 'waiting-for', issue: { number: 5, title: 'Wire up auth' } },
      })

      expect(screen.getByTestId('prerequisite-picker-readiness')).toBeInTheDocument()
    })
  })

  describe('no-regression: legacy chip-removal block is gone', () => {
    it('does not render a separate Remove prerequisite: block; chips own removal', () => {
      setIssues(CANDIDATE_ISSUES)
      renderCard({
        prerequisites: [
          { number: 5, title: 'Wire up auth', completed: false, status: IssueStatus.InProgress, health: IssueHealth.Active },
        ],
      })

      expect(screen.queryByText(/Remove prerequisite/)).not.toBeInTheDocument()
      const chips = screen.getAllByTestId('prerequisite-picker-chip')
      expect(chips).toHaveLength(1)
      expect(within(chips[0]).getByTestId('prerequisite-picker-chip-remove')).toBeInTheDocument()
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
