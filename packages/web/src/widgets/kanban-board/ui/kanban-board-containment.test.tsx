// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import * as React from 'react'
import { cleanup, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import fs from 'node:fs'
import path from 'node:path'
import { SidebarInset } from '@/shared/ui/components/sidebar'
import { KanbanBoard } from './KanbanBoard'
import type { AgentStatus } from '../../../entities/agent'
import {
  IssueStatus,
  IssueHealth,
  type Issue,
} from '../../../entities/issue'

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    archiveIssue: () => Promise.resolve({ archived: 0, skipped: 0 }),
    rerunIssue: () => Promise.resolve(),
    resumeIssue: () => Promise.resolve(),
    useLabels: () => ({ data: [], isLoading: false }),
  }
})

vi.mock('../../../entities/runner/api/queries', () => ({
  useRunnerSummary: () => ({
    hasConnectedCapacity: true,
    connectedIdleCount: 1,
    connectedBusyCount: 0,
    rows: [],
  }),
}))

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: `issue-${Math.random().toString(36).slice(2)}`,
    number: 1,
    title: 'Test Issue',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    priority: 'p2',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

const mockAgentStatus: AgentStatus = {
  running: false,
  issueId: null,
  issueNumber: null,
  activeAgents: [],
  capacity: { active: 0, max: 2 },
}

function Probe({ children }: { children: React.ReactNode }) {
  return <SidebarInset>{children}</SidebarInset>
}

afterEach(() => {
  cleanup()
})

describe('Issue board desktop horizontal scroll containment', () => {
  it('applies min-w-0 to SidebarInset <main> so the flex chain can shrink', () => {
    render(
      <Probe>
        <div data-testid="probe-child" />
      </Probe>,
    )

    const sidebarInset = document.querySelector<HTMLElement>('[data-slot="sidebar-inset"]')
    expect(sidebarInset).not.toBeNull()

    const classes = sidebarInset!.className.split(/\s+/)
    expect(classes).toContain('flex')
    expect(classes).toContain('flex-col')
    expect(classes).toContain('min-w-0')
  })

  it('applies min-w-0 to the KanbanBoard root container and keeps the row as the overflow owner', () => {
    const queryClient = new QueryClient()
    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <KanbanBoard issues={[makeIssue()]} agentStatus={mockAgentStatus} />
        </MemoryRouter>
      </QueryClientProvider>,
    )

    const root = screen.getByTestId('kanban-board-root')
    expect(root.className.split(/\s+/)).toContain('min-w-0')
    expect(root.className.split(/\s+/)).toContain('flex')

    const row = screen.getByTestId('kanban-board-row')
    expect(row.className.split(/\s+/)).toContain('overflow-x-auto')
    expect(row.className.split(/\s+/)).toContain('min-w-0')
  })

  it('pins min-w-0 on the App.tsx content wrapper inside SidebarInset', () => {
    const appSource = fs.readFileSync(
      path.resolve(__dirname, '../../../app/App.tsx'),
      'utf8',
    )

    const wrapperMatch = appSource.match(
      /<SidebarInset>\s*<Header[\s\S]*?\/>\s*<div([^>]*)>/,
    )
    expect(wrapperMatch).not.toBeNull()

    const wrapperAttrs = wrapperMatch![1]
    expect(wrapperAttrs).toContain('min-w-0')
    expect(wrapperAttrs).toContain('flex-1')
    expect(wrapperAttrs).toContain('min-h-0')
  })
})
