import { afterEach, describe, expect, it } from 'vitest'

import { screen } from '@testing-library/react'

import { EpicStatus, type EpicDetail } from '../../../entities/epic'

import { issues, linkedIssue, renderPage, getActionGroup, getMobileHeaderContainer, getEpicDetailPageContainer, getTitleBlock } from './_epicDetailPageTestUtils'

describe('EpicDetailPage mobile layout structural contract', () => {
  const LONG_CHINESE_TITLE =
    '史诗详情页移动端布局修复：消除横向溢出与标题压缩，让标题和描述在窄屏下独占可读宽度，操作按钮按主次分级可见'
  const LONG_ENGLISH_TITLE =
    'EpicDetailPageMobileHeaderTitleWithAnUnbrokenEnglishTokenThatMustWrapInsideTheReadableColumnAtThreeHundredTwentyPixels'
  const LONG_ENGLISH_DESCRIPTION =
    'EpicDetailPageMobileHeaderDescriptionWithAnUnbrokenEnglishTokenThatMustWrapInsideTheDescriptionColumnAtThreeHundredTwentyPixels'

  function makeEpic(overrides: Record<string, unknown> = {}): EpicDetail {
    return {
      projectId: 'proj-1',
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
    } as EpicDetail
  }

  let currentEpic = makeEpic()

  function mockEpic(epic: EpicDetail) {
    currentEpic = epic
  }

  afterEach(() => {
    mockEpic(makeEpic())
  })

  async function renderPageReady() {
    renderPage({ epic: currentEpic, issues })
    await screen.findByTestId('epic-number')
  }

  it('uses a flex-col mobile layout and md:flex-row desktop layout in the header container', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Running }))
    await renderPageReady()

    const header = getMobileHeaderContainer()
    expect(header.classList.contains('flex')).toBe(true)
    expect(header.classList.contains('flex-col')).toBe(true)
    expect(header.classList.contains('gap-4')).toBe(true)
    expect(header.classList.contains('md:flex-row')).toBe(true)
    expect(header.classList.contains('md:flex-wrap')).toBe(true)
    expect(header.classList.contains('md:items-start')).toBe(true)
    expect(header.classList.contains('md:justify-between')).toBe(true)
  })

  it('lets the page wrapper shrink inside the app shell at mobile widths', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Running }))
    await renderPageReady()

    const container = getEpicDetailPageContainer()
    expect(container.classList.contains('w-full')).toBe(true)
    expect(container.classList.contains('min-w-0')).toBe(true)
    expect(container.classList.contains('max-w-4xl')).toBe(true)
  })

  it('places the title block before the action button group in DOM order on a running epic so it stacks above on mobile', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Running, title: LONG_CHINESE_TITLE }))
    await renderPageReady()

    const header = getMobileHeaderContainer()
    const titleBlock = getTitleBlock()
    const actionGroup = getActionGroup()

    const titleIndex = Array.from(header.children).indexOf(titleBlock)
    const actionIndex = Array.from(header.children).indexOf(actionGroup)
    expect(titleIndex).toBeGreaterThanOrEqual(0)
    expect(actionIndex).toBeGreaterThanOrEqual(0)
    expect(titleIndex).toBeLessThan(actionIndex)
  })

  it('places the title block before the action button group in DOM order on an idle epic', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Idle, title: LONG_ENGLISH_TITLE }))
    await renderPageReady()

    const header = getMobileHeaderContainer()
    const titleBlock = getTitleBlock()
    const actionGroup = getActionGroup()

    const titleIndex = Array.from(header.children).indexOf(titleBlock)
    const actionIndex = Array.from(header.children).indexOf(actionGroup)
    expect(titleIndex).toBeLessThan(actionIndex)
  })

  it('keeps the title block class contract (min-w-0 + flex-1) so it can shrink/wrap on mobile', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Running, title: LONG_CHINESE_TITLE }))
    await renderPageReady()

    const titleBlock = getTitleBlock()
    expect(titleBlock.classList.contains('min-w-0')).toBe(true)
    expect(titleBlock.classList.contains('flex-1')).toBe(true)
  })

  it('adds an explicit break rule to an unbroken English title so it cannot force horizontal overflow', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Idle, title: LONG_ENGLISH_TITLE }))
    await renderPageReady()

    const heading = screen.getByRole('heading', { name: LONG_ENGLISH_TITLE })
    expect(heading.classList.contains('[overflow-wrap:anywhere]')).toBe(true)
  })

  it('adds an explicit break rule to plain description content with an unbroken English token', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Running, description: LONG_ENGLISH_DESCRIPTION }))
    await renderPageReady()

    const description = screen.getByTestId('epic-description')
    expect(description.classList.contains('[overflow-wrap:anywhere]')).toBe(true)
    expect(description).toHaveTextContent(LONG_ENGLISH_DESCRIPTION)
  })

  it('uses flex-wrap on the action button group so secondary actions stay reachable on mobile', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Running }))
    await renderPageReady()

    const actionGroup = getActionGroup()
    expect(actionGroup.classList.contains('flex')).toBe(true)
    expect(actionGroup.classList.contains('flex-wrap')).toBe(true)
    expect(actionGroup.classList.contains('justify-start')).toBe(true)
    expect(actionGroup.classList.contains('md:justify-end')).toBe(true)
  })

  it('renders the running lifecycle action (Pause) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Running }))
    await renderPageReady()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
  })

  it('renders the idle lifecycle action (Start Epic) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Idle }))
    await renderPageReady()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
  })

  it('renders the paused lifecycle action (Resume) and keeps Edit/Mark Done/Close Epic reachable in the action group on mobile', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Paused }))
    await renderPageReady()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeTruthy()

    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
  })

  it('omits Start/Pause/Resume lifecycle actions for a done epic on mobile', async () => {
    mockEpic(makeEpic({
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
    }))
    await renderPageReady()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="reopen-epic-trigger"]')).toBeTruthy()
  })

  it('omits Start/Pause/Resume lifecycle actions for a closed epic on mobile', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Closed }))
    await renderPageReady()

    const actionGroup = getActionGroup()

    expect(actionGroup.querySelector('[data-testid="edit-epic-button"]')).toBeTruthy()
    expect(actionGroup.querySelector('[data-testid="start-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="pause-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="resume-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="mark-epic-done"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="close-epic-trigger"]')).toBeNull()
    expect(actionGroup.querySelector('[data-testid="reopen-epic-trigger"]')).toBeTruthy()
  })

  it('uses flex-wrap on the LinkedIssueRow action container so Start/Remove can wrap at 320px', async () => {
    mockEpic(makeEpic({
      status: EpicStatus.Running,
      linkedIssues: [
        linkedIssue({ number: 3, title: 'Candidate issue' }),
      ],
    }))
    await renderPageReady()

    const linkedStartButton = await screen.findByTestId('linked-issue-start')
    const actionContainer = linkedStartButton.parentElement as HTMLElement
    expect(actionContainer).toBeTruthy()
    expect(actionContainer.getAttribute('data-testid')).toBe('linked-issue-actions-row')
    expect(actionContainer.classList.contains('flex')).toBe(true)
    expect(actionContainer.classList.contains('flex-wrap')).toBe(true)
    expect(actionContainer.classList.contains('gap-2')).toBe(true)
  })

  it('keeps the desktop flex-row + justify-between classes on the header container for >=md layout', async () => {
    mockEpic(makeEpic({ status: EpicStatus.Running }))
    await renderPageReady()

    const header = getMobileHeaderContainer()
    expect(header.classList.contains('md:flex-row')).toBe(true)
    expect(header.classList.contains('md:justify-between')).toBe(true)
    expect(header.classList.contains('md:items-start')).toBe(true)
    expect(header.classList.contains('md:flex-wrap')).toBe(true)
  })
})
