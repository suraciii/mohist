import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, screen, waitFor, within } from '@testing-library/react'
import {
  DEFAULT_RECOVERY,
  makeIssue,
  mockMatchMedia,
  renderPage,
} from './_issueDetailReferenceRailTestUtils'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'


mountIssueDetail({ issue: makeIssue() })

beforeEach(() => {
  mockMatchMedia(false)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('IssueDetailPage reference-rail — lightest visual weight', () => {
  it('ranks rail below reading flow and reading flow below status headline by data-tier-weight', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

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

  it('uses desktop sticky positioning without applying sticky behavior on narrow viewports', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.className).toContain('lg:sticky')
    expect(referenceRail.className).toContain('lg:top-6')
    expect(referenceRail.className).toContain('lg:self-start')
    expect(referenceRail.className).toContain('lg:max-h-[calc(100vh-3rem)]')
    expect(referenceRail.className).toContain('lg:overflow-y-auto')
  })

  it('does not apply desktop sticky classes to the narrow rail', async () => {
    mockMatchMedia(true)
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    expect(referenceRail.className).not.toMatch(/(?:^|\s)sticky(?:\s|$)/)
  })


  it('does not nest same-name CardSection chrome inside expanded rail cards', async () => {
    mockIssue(makeIssue({
      model: 'sonnet',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
      recovery: DEFAULT_RECOVERY,
    }))

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
  })
})

describe('IssueDetailPage reference-rail — lightest visual weight', () => {
  it('does not apply heavy-fill or shadow chrome to the rail container or its cards', async () => {
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

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
    mockIssue(makeIssue({ recovery: DEFAULT_RECOVERY }))

    renderPage()

    const referenceRail = await waitFor(() => screen.getByTestId('reference-rail'))
    const detailsToggle = screen.getByTestId('reference-rail-details-toggle')
    expect(detailsToggle.className).toMatch(/text-muted-foreground/)
    expect(detailsToggle.className).not.toMatch(/text-foreground(\b|[^/])/)
    expect(referenceRail.contains(detailsToggle)).toBe(true)
  })
})
