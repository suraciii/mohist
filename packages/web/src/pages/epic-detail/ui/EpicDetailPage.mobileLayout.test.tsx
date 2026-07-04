// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, screen } from '@testing-library/react'

import { EpicStatus } from '../../../entities/epic'

import { issues, linkedIssue, renderPage, getActionGroup, getMobileHeaderContainer, getEpicDetailPageContainer, getTitleBlock } from './_epicDetailPageTestHarness'

/**
 * Page-level mobile layout structural-contract tests for <EpicDetailPage/>.
 */

const mocks = vi.hoisted(() => ({
  useProject: vi.fn(),
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

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useProject: mocks.useProject,
  }
})
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

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => vi.fn(),
  }
})

describe('EpicDetailPage mobile layout structural contract', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  const LONG_CHINESE_TITLE =
    '史诗详情页移动端布局修复：消除横向溢出与标题压缩，让标题和描述在窄屏下独占可读宽度，操作按钮按主次分级可见'
  const LONG_ENGLISH_TITLE =
    'EpicDetailPageMobileHeaderTitleWithAnUnbrokenEnglishTokenThatMustWrapInsideTheReadableColumnAtThreeHundredTwentyPixels'
  const LONG_ENGLISH_DESCRIPTION =
    'EpicDetailPageMobileHeaderDescriptionWithAnUnbrokenEnglishTokenThatMustWrapInsideTheDescriptionColumnAtThreeHundredTwentyPixels'

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: 7,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Idle,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 0,
        totalIssueCount: 0,
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

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
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

  it('uses a flex-col mobile layout and md:flex-row desktop layout in the header container', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const header = getMobileHeaderContainer()
    expect(header.classList.contains('flex')).toBe(true)
    expect(header.classList.contains('flex-col')).toBe(true)
    expect(header.classList.contains('gap-4')).toBe(true)
    expect(header.classList.contains('md:flex-row')).toBe(true)
    expect(header.classList.contains('md:flex-wrap')).toBe(true)
    expect(header.classList.contains('md:items-start')).toBe(true)
    expect(header.classList.contains('md:justify-between')).toBe(true)
  })

  it('lets the page wrapper shrink inside the app shell at mobile widths', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const container = getEpicDetailPageContainer()
    expect(container.classList.contains('w-full')).toBe(true)
    expect(container.classList.contains('min-w-0')).toBe(true)
    expect(container.classList.contains('max-w-4xl')).toBe(true)
  })

  it('places the title block before the action button group in DOM order on a running epic so it stacks above on mobile', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running, title: LONG_CHINESE_TITLE }),
      isLoading: false,
    })

    renderPage()

    const header = getMobileHeaderContainer()
    const titleBlock = getTitleBlock()
    const actionGroup = getActionGroup()

    const titleIndex = Array.from(header.children).indexOf(titleBlock)
    const actionIndex = Array.from(header.children).indexOf(actionGroup)
    expect(titleIndex).toBeGreaterThanOrEqual(0)
    expect(actionIndex).toBeGreaterThanOrEqual(0)
    expect(titleIndex).toBeLessThan(actionIndex)
  })

  it('places the title block before the action button group in DOM order on an idle epic', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Idle, title: LONG_ENGLISH_TITLE }),
      isLoading: false,
    })

    renderPage()

    const header = getMobileHeaderContainer()
    const titleBlock = getTitleBlock()
    const actionGroup = getActionGroup()

    const titleIndex = Array.from(header.children).indexOf(titleBlock)
    const actionIndex = Array.from(header.children).indexOf(actionGroup)
    expect(titleIndex).toBeLessThan(actionIndex)
  })

  it('keeps the title block class contract (min-w-0 + flex-1) so it can shrink/wrap on mobile', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running, title: LONG_CHINESE_TITLE }),
      isLoading: false,
    })

    renderPage()

    const titleBlock = getTitleBlock()
    expect(titleBlock.classList.contains('min-w-0')).toBe(true)
    expect(titleBlock.classList.contains('flex-1')).toBe(true)
  })

  it('adds an explicit break rule to an unbroken English title so it cannot force horizontal overflow', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Idle, title: LONG_ENGLISH_TITLE }),
      isLoading: false,
    })

    renderPage()

    const heading = screen.getByRole('heading', { name: LONG_ENGLISH_TITLE })
    expect(heading.classList.contains('[overflow-wrap:anywhere]')).toBe(true)
  })

  it('adds an explicit break rule to plain description content with an unbroken English token', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({ status: EpicStatus.Running, description: LONG_ENGLISH_DESCRIPTION }),
      isLoading: false,
    })

    renderPage()

    const description = screen.getByTestId('epic-description')
    expect(description.classList.contains('[overflow-wrap:anywhere]')).toBe(true)
    expect(description).toHaveTextContent(LONG_ENGLISH_DESCRIPTION)
  })

  it('uses flex-wrap on the action button group so secondary actions stay reachable on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()
    expect(actionGroup.classList.contains('flex')).toBe(true)
    expect(actionGroup.classList.contains('flex-wrap')).toBe(true)
    expect(actionGroup.classList.contains('justify-start')).toBe(true)
    expect(actionGroup.classList.contains('md:justify-end')).toBe(true)
  })

  it('renders the running lifecycle action (Pause) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
  })

  it('renders the idle lifecycle action (Start Epic) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Idle }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
  })

  it('renders the paused lifecycle action (Resume) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Paused }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
  })

  it('omits Start/Pause/Resume lifecycle actions for a done epic on mobile', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Done,
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
      }),
      isLoading: false,
    })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeNull()
  })

  it('omits Start/Pause/Resume lifecycle actions for a closed epic on mobile', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Closed }), isLoading: false })

    renderPage()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeNull()
  })

  it('uses flex-wrap on the LinkedIssueRow action container so Start/Remove can wrap at 320px', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        status: EpicStatus.Running,
        linkedIssues: [
          linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' }),
        ],
      }),
      isLoading: false,
    })

    renderPage()

    const linkedStartButton = screen.getByTestId('linked-issue-start')
    const actionContainer = linkedStartButton.parentElement as HTMLElement
    expect(actionContainer).toBeTruthy()
    expect(actionContainer.getAttribute('data-testid')).toBe('linked-issue-actions-row')
    expect(actionContainer.classList.contains('flex')).toBe(true)
    expect(actionContainer.classList.contains('flex-wrap')).toBe(true)
    expect(actionContainer.classList.contains('gap-2')).toBe(true)
  })

  it('keeps the desktop flex-row + justify-between classes on the header container for >=md layout', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic({ status: EpicStatus.Running }), isLoading: false })

    renderPage()

    const header = getMobileHeaderContainer()
    expect(header.classList.contains('md:flex-row')).toBe(true)
    expect(header.classList.contains('md:justify-between')).toBe(true)
    expect(header.classList.contains('md:items-start')).toBe(true)
    expect(header.classList.contains('md:flex-wrap')).toBe(true)
  })
})
