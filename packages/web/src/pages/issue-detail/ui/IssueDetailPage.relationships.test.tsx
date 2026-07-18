import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueDetailPage } from './IssueDetailPage'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'

const projects: Project[] = [{
  id: 'proj-1',
  name: 'Project 1',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  repositories: [],
}]

function makeIssue(overrides: Record<string, unknown> = {}) {
  return {
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'backlog',
    health: 'active',
    projectId: 'proj-1',
    labels: [],
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
        <MemoryRouter initialEntries={['/issues/14']}>
            <Routes><Route path="/issues/:number" element={<IssueDetailPage />} /></Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(cleanup)
mountIssueDetail({ issue: makeIssue() })

describe('IssueDetailPage parent-child relationship display', () => {
  it('renders a child parent reference in the details rail', async () => {
    mockIssue(makeIssue({ parentIssueRef: { number: 42, title: 'Parent issue' } }))
    renderPage()
    expect(await waitFor(() => screen.getByTestId('parent-issue-metadata-row'))).toHaveTextContent('#42 Parent issue')
  })

  it('renders the parent indicator and child count in the details rail', async () => {
    mockIssue(makeIssue({ childIssuesSummary: { hasChildren: true, count: 3 } }))
    renderPage()
    expect(await waitFor(() => screen.getByTestId('child-issues-metadata-row'))).toHaveTextContent('3 child issues')
  })
})
