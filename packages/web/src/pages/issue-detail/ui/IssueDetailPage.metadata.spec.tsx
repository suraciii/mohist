import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider, type Project } from '../../../entities/project'
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
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    ...overrides,
  }
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/issues/14']}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

mountIssueDetail({ issue: makeIssue() })
afterEach(cleanup)

describe('IssueDetailPage issue body metadata', () => {
  it('renders sanitized description and recommendation metadata without overriding current state', async () => {
    mockIssue(makeIssue({
      workflowProfileId: 'selected/profile',
      risk: 'high',
      body: [
        '---',
        'recommended_workflow: recommended/profile',
        'recommended_workflow_reason: "Template recommendation"',
        'risk: low',
        '---',
        'Visible description content',
      ].join('\n'),
    }))
    renderPage()

    const description = await waitFor(() => screen.getByTestId('description-section'))
    expect(within(description).getByText('Visible description content')).toBeTruthy()
    expect(within(description).queryByText(/recommended_workflow/)).toBeNull()
    const details = screen.getByTestId('issue-detail-details-metadata')
    expect(within(details).getByText('Recommended workflow')).toBeTruthy()
    expect(within(details).getByText('recommended/profile')).toBeTruthy()
    expect(within(details).getByText('Template recommendation')).toBeTruthy()
    expect(within(details).getAllByText('Risk')).toHaveLength(1)
    expect(within(details).getByText('high')).toBeTruthy()
    expect(within(details).queryByText('low')).toBeNull()
    expect(screen.getByTestId('reference-rail-workflow-profile')).toHaveTextContent('selected/profile')
  })

  it('shows recognized metadata but no Description for an envelope-only body', async () => {
    mockIssue(makeIssue({ body: ['---', 'risk: medium', '---'].join('\n') }))
    renderPage()

    const details = await waitFor(() => screen.getByTestId('issue-detail-details-metadata'))
    expect(within(details).getByText('medium')).toBeTruthy()
    expect(screen.queryByTestId('description-section')).toBeNull()
    expect(screen.queryByText('risk: medium')).toBeNull()
  })

  it('keeps bounded malformed metadata hidden while retaining its description', async () => {
    mockIssue(makeIssue({
      body: ['---', 'malformed line', 'risk: low', '---', 'Retained description'].join('\n'),
    }))
    renderPage()

    const description = await waitFor(() => screen.getByTestId('description-section'))
    expect(within(description).getByText('Retained description')).toBeTruthy()
    expect(screen.queryByText('malformed line')).toBeNull()
    expect(screen.queryByTestId('risk-metadata-row')).toBeNull()
  })

  it('hides an unclosed envelope from description, preview, and editor', async () => {
    mockIssue(makeIssue({ body: ['---', 'risk: medium', 'raw internal text'].join('\n') }))
    renderPage()

    await waitFor(() => expect(screen.getByRole('button', { name: 'Edit issue' })).toBeTruthy())
    expect(screen.queryByTestId('description-section')).toBeNull()
    expect(screen.queryByTestId('description-preview-hint')).toBeNull()
    expect(screen.queryByText('raw internal text')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Edit issue' }))
    expect((await screen.findByPlaceholderText('Optional description') as HTMLTextAreaElement).value).toBe('')
  })
})
