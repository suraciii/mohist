// Regression: the epic detail API omits nullable fields (startBlocker, nextIssueReason)
// when they are null. The page must tolerate their absence (undefined, not null) without
// crashing, and still identify the startable next issue. All fixture data below is
// synthetic and unrelated to any real epic/issue.
import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import type { EpicDetail } from '../../../entities/epic'
import { renderPage } from './_epicDetailPageTestUtils'

const epic = {
  number: 7,
  title: 'Fixture epic',
  description: 'Fixture description',
  priority: 'p2',
  status: 'paused',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  progress: {
    deliveredCount: 1,
    totalIssueCount: 2,
    blockedIssues: [],
    activeIssues: [],
    nextIssue: { number: 2, title: 'Fixture backlog issue' },
    readyToMarkDone: false,
  },
  linkedIssues: [
    { number: 1, title: 'Fixture done issue', status: 'done', stage: 'done', health: 'done', priority: 'p2', canStart: false, prerequisiteNumbers: [], externalPrerequisites: [] },
    // backlog, startable, and startBlocker is OMITTED (the regression trigger)
    { number: 2, title: 'Fixture backlog issue', status: 'backlog', stage: '', health: 'active', priority: 'p2', canStart: true, prerequisiteNumbers: [], externalPrerequisites: [] },
  ],
} as unknown as EpicDetail

describe('EpicDetailPage when the API omits nullable fields', () => {
  it('renders without crashing and identifies the startable next issue', async () => {
    renderPage({ epic, issues: [] })

    expect(await screen.findByText('Fixture epic')).toBeTruthy()
    // startBlocker omitted -> the backlog issue is correctly seen as startable
    expect(await screen.findByTestId('next-issue')).toHaveTextContent('#2')
  })
})
