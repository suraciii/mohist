// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { RunnerList } from './RunnerList'
import type { RunnerStatusRow } from '../../../entities/runner'

vi.mock('../../../entities/project', () => ({
  useProjectPath: () => (path: string) => `/demo${path}`,
}))

function makeRunner(overrides: Partial<RunnerStatusRow> = {}): RunnerStatusRow {
  return {
    id: 'runner-1',
    status: 'busy',
    scope: { type: 'project', projectId: 'proj-1', projectName: 'demo' },
    hostname: 'host-1',
    lastHeartbeatAt: '2026-07-08T00:00:00.000Z',
    connectionState: 'connected',
    kind: 'embedded',
    capabilities: [],
    coderModels: [],
    coderModelCount: 0,
    activeWorks: [
      {
        workId: 'work-1',
        workType: 'issue',
        ownerKind: 'issue',
        ownerId: 'issue-1',
        title: 'Active issue',
        issue: { projectId: 'proj-1', issueId: 'issue-1', issueNumber: 42 },
        stage: 'build',
      },
    ],
    capacity: { usedSlots: 1, totalSlots: 4 },
    ...overrides,
  }
}

function renderList(rows: RunnerStatusRow[]) {
  return render(
    <MemoryRouter>
      <RunnerList rows={rows} />
    </MemoryRouter>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('RunnerList - active work row uses semantic tokens', () => {
  it('renders the active work issue link with primary token and no raw blue palette', () => {
    const runner = makeRunner()
    renderList([runner])

    const link = screen.getByTestId('active-work-issue-link')
    expect(link.className).toContain('text-primary')
    expect(link.className).toContain('hover:text-primary/80')
    expect(link.className).not.toContain('text-blue-')
  })

  it('renders the active work separator with muted-foreground and no raw gray palette', () => {
    const runner = makeRunner()
    renderList([runner])

    const row = screen.getByTestId('active-work-row')
    const separators = row.querySelectorAll('span.text-muted-foreground')
    const separator = Array.from(separators).find((el) => el.textContent === '·')
    expect(separator).toBeTruthy()
    const rowHtml = row.innerHTML
    expect(rowHtml).not.toContain('text-gray-')
  })

  it('active work row avoids raw blue and gray palette classes across link and separators', () => {
    const runner = makeRunner()
    const { container } = renderList([runner])
    const html = container.innerHTML
    expect(html).toContain('text-primary')
    expect(html).toContain('text-muted-foreground')
    expect(html).not.toContain('text-blue-')
    expect(html).not.toContain('text-gray-')
  })
})
