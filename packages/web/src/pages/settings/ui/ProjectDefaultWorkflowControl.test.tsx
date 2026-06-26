// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project/model/ProjectContext'
import { WorkflowProfilesSection } from './WorkflowProfilesSection'

const SYSTEM_TEMPLATES = [
  {
    id: 'mohist/local',
    name: 'Mohist Local',
    description: 'Standard staged workflow.',
    isDefault: true,
  },
  {
    id: 'mohist/github-pr',
    name: 'Mohist GitHub PR',
    description: 'Pull-request oriented workflow.',
    isDefault: false,
  },
  {
    id: 'mohist/quick-fix',
    name: 'Mohist Quick Fix',
    description: 'Short repair workflow.',
    isDefault: false,
  },
]

let currentDefault: string | null = null
const requests: { method: string; url: string; body: unknown }[] = []

const handlers = [
  http.get('/api/workflow-templates/system', () =>
    HttpResponse.json({ success: true, data: SYSTEM_TEMPLATES }),
  ),
  http.get('/api/projects/test-project/workflow-profile', () =>
    HttpResponse.json({
      success: true,
      data: { projectId: 'test-project', defaultTemplateId: currentDefault },
    }),
  ),
  http.put('/api/projects/test-project/workflow-profile/default-template', async ({ request }) => {
    const body = await request.json()
    requests.push({ method: 'PUT', url: request.url, body })
    currentDefault = (body as { templateId: string }).templateId
    return HttpResponse.json({
      success: true,
      data: { projectId: 'test-project', defaultTemplateId: currentDefault },
    })
  }),
  http.delete('/api/projects/test-project/workflow-profile/default-template', ({ request }) => {
    requests.push({ method: 'DELETE', url: request.url, body: null })
    currentDefault = null
    return HttpResponse.json({
      success: true,
      data: { projectId: 'test-project', defaultTemplateId: null },
    })
  }),
]

const server = setupServer(...handlers)

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})

afterAll(() => {
  server.close()
})

beforeEach(() => {
  currentDefault = null
  requests.length = 0
  server.resetHandlers(...handlers)
})

afterEach(() => {
  server.resetHandlers(...handlers)
})

function renderSection() {
  return render(<WorkflowProfilesSection />)
}

function renderWithoutProject() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={null} initialProjects={[]}>
        <WorkflowProfilesSection />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('ProjectDefaultWorkflowControl', () => {
  it('renders a no-project-selected hint when there is no active project', () => {
    renderWithoutProject()

    expect(screen.getByTestId('project-default-workflow-no-project')).toHaveTextContent(
      'No project selected',
    )
  })

  it('displays the current project default read back from the workflow-profile endpoint', async () => {
    currentDefault = 'mohist/github-pr'
    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-value')).toHaveTextContent('mohist/github-pr'),
    )
  })

  it('selecting mohist/github-pr sends PUT and reads back the new default', async () => {
    currentDefault = null
    renderSection()

    const select = await waitFor(() =>
      screen.getByTestId('project-default-workflow-select'),
    )

    fireEvent.change(select, { target: { value: 'mohist/github-pr' } })

    await waitFor(() => expect(requests).toHaveLength(1),
    )
    expect(requests[0].method).toBe('PUT')
    expect(requests[0].body).toEqual({ templateId: 'mohist/github-pr' })

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-value')).toHaveTextContent('mohist/github-pr'),
    )
  })

  it('clearing the project default sends DELETE and returns to the inherit state', async () => {
    currentDefault = 'mohist/github-pr'
    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-value')).toHaveTextContent('mohist/github-pr'),
    )

    fireEvent.click(screen.getByTestId('project-default-workflow-clear'))

    await waitFor(() => expect(requests).toHaveLength(1),
    )
    expect(requests[0].method).toBe('DELETE')

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-system-default')).toHaveTextContent(
        'mohist/local',
      ),
    )
    expect(screen.queryByTestId('project-default-workflow-value')).not.toBeInTheDocument()
  })

  it('selecting inherit system default sends DELETE and returns to the inherit state', async () => {
    currentDefault = 'mohist/github-pr'
    renderSection()

    const select = await screen.findByTestId('project-default-workflow-select') as HTMLSelectElement
    await waitFor(() => expect(select.value).toBe('mohist/github-pr'))

    fireEvent.change(select, { target: { value: '' } })

    await waitFor(() => expect(requests).toHaveLength(1))
    expect(requests[0].method).toBe('DELETE')

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-system-default')).toHaveTextContent(
        'mohist/local',
      ),
    )
    expect(select.value).toBe('')
  })

  it('warns when the configured default is absent from the system catalog and offers Clear', async () => {
    currentDefault = 'mohist/unknown'
    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-orphan-warning')).toHaveTextContent(
        'mohist/unknown',
      ),
    )

    expect(screen.getByTestId('project-default-workflow-clear')).not.toBeDisabled()
  })

  it('renders the system-default badge with styling distinct from the project-default badge', async () => {
    currentDefault = 'mohist/github-pr'
    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-value')).toHaveTextContent('mohist/github-pr'),
    )
    await waitFor(() =>
      expect(screen.getByTestId('workflow-profile-mohist/local')).toBeInTheDocument(),
    )

    const defaultCard = screen.getByTestId('workflow-profile-mohist/local')
    const systemBadge = within(defaultCard).getByText('System default')
    expect(systemBadge.className).toContain('bg-slate-50')
    expect(systemBadge.className).toContain('text-slate-700')

    const projectBadge = screen.getByText('Project default')
    expect(projectBadge.className).toContain('bg-green-50')
    expect(projectBadge.className).toContain('text-green-700')
  })
})
