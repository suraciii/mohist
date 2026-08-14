import { beforeEach, describe, expect, it } from 'vitest'
import { render, screen, waitFor, within } from '../../../../tests/test-utils'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
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
let currentDisabledIds: string[] = []
const requests: { method: string; url: string; body: unknown }[] = []

const handlers = [
  http.get('/api/projects/test-project/workflow-profiles', () => {
    return HttpResponse.json({
      success: true,
      data: SYSTEM_TEMPLATES.map((template) => ({
        projectId: 'test-project',
        profileId: template.id,
        name: template.name,
        description: template.description,
        sourceProvenance: 'BuiltIn',
        isBuiltIn: true,
        definitionSource: null,
        agentRuntime: 'opencode',
      })),
    })
  }),
  http.get(/\/api\/projects\/test-project\/workflow-profiles\/.+$/, ({ request }) => {
    const profileId = decodeURIComponent(new URL(request.url).pathname.split('/workflow-profiles/')[1] ?? '')
    const profile = SYSTEM_TEMPLATES.find((template) => template.id === profileId)
    return HttpResponse.json({
      success: true,
      data: {
        projectId: 'test-project',
        profileId,
        name: profile?.name ?? profileId,
        description: profile?.description ?? '',
        sourceProvenance: 'BuiltIn',
        isBuiltIn: true,
        definitionSource: `id: ${profileId}`,
        agentRuntime: 'opencode',
        stages: [],
      },
    })
  }),
  http.get('/api/projects/test-project/workflow-profile/default', () =>
    HttpResponse.json({
      success: true,
      data: { projectId: 'test-project', defaultWorkflowProfileId: currentDefault, disabledWorkflowProfileIds: currentDisabledIds },
    }),
  ),
  http.put('/api/projects/test-project/workflow-profile/default', async ({ request }) => {
    const body = await request.json()
    requests.push({ method: 'PUT', url: request.url, body })
    currentDefault = (body as { profileId: string }).profileId
    return HttpResponse.json({
      success: true,
      data: { projectId: 'test-project', profileId: currentDefault },
    })
  }),
  http.post('/api/projects/test-project/workflow-profile/disable', async ({ request }) => {
    const body = await request.json() as { profileId: string }
    requests.push({ method: 'POST', url: request.url, body })
    currentDisabledIds = Array.from(new Set([...currentDisabledIds, body.profileId]))
    return HttpResponse.json({ success: true, data: null })
  }),
  http.post('/api/projects/test-project/workflow-profile/enable', async ({ request }) => {
    const body = await request.json() as { profileId: string }
    requests.push({ method: 'POST', url: request.url, body })
    currentDisabledIds = currentDisabledIds.filter((id) => id !== body.profileId)
    return HttpResponse.json({ success: true, data: null })
  }),
]

useMswServer(...handlers)

beforeEach(() => {
  currentDefault = null
  currentDisabledIds = []
  requests.length = 0
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
  it('renders a no-project CTA when the WorkflowProfilesSection is mounted without a project', () => {
    renderWithoutProject()

    expect(screen.getByTestId('no-project-select-button')).toBeInTheDocument()
    expect(screen.getByTestId('no-project-create-button')).toBeInTheDocument()
    expect(screen.queryByTestId('project-default-workflow-no-project')).not.toBeInTheDocument()
  })

  it('displays the current project default read back from the workflow-profile endpoint', async () => {
    currentDefault = 'mohist/github-pr'
    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-value')).toHaveTextContent('mohist/github-pr'),
    )
  })

  it('selecting mohist/github-pr sends PUT and reads back the new default', async () => {
    const user = userEvent.setup()
    currentDefault = null
    renderSection()

    const trigger = await waitFor(() =>
      screen.getByTestId('project-default-workflow-select'),
    )

    await user.click(trigger)

    const option = await screen.findByRole('option', { name: /Mohist GitHub PR/ })
    await user.click(option)

    await waitFor(() => expect(requests).toHaveLength(1),
    )
    expect(requests[0].method).toBe('PUT')
    expect(requests[0].body).toEqual({ profileId: 'mohist/github-pr' })

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-value')).toHaveTextContent('mohist/github-pr'),
    )
  })

  it('warns when the configured default is absent from the project catalog', async () => {
    currentDefault = 'mohist/unknown'
    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-orphan-warning')).toHaveTextContent(
        'mohist/unknown',
      ),
    )

    expect(screen.queryByTestId('project-default-workflow-clear')).not.toBeInTheDocument()
  })

  it('shows the inherited default from the filtered enabled profiles when the system default is disabled', async () => {
    currentDefault = null
    currentDisabledIds = ['mohist/local']

    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-system-default')).toHaveTextContent(
        'mohist/github-pr',
      ),
    )
  })

  it('shows an error when the project-scoped enabled profiles query fails', async () => {
    server.use(
      http.get('/api/projects/test-project/workflow-profiles', () =>
        HttpResponse.json({ success: false, error: 'boom' }, { status: 500 }),
      ),
    )

    renderSection()

    await waitFor(() =>
      expect(screen.getByText('Failed to load workflow profile settings.')).toBeInTheDocument(),
    )
    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
  })

  it('shows an amber warning and disabled dropdown item when the configured default is disabled', async () => {
    const user = userEvent.setup()
    currentDefault = 'mohist/local'
    currentDisabledIds = ['mohist/local']

    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-disabled-warning')).toHaveTextContent(
        'mohist/local',
      ),
    )

    await user.click(screen.getByTestId('project-default-workflow-select'))

    const disabledOption = await screen.findByRole('option', { name: /Mohist Local/ })
    expect(disabledOption).toHaveAttribute('aria-disabled', 'true')
    expect(within(disabledOption).getByText('Mohist Local')).toHaveClass('text-muted-foreground')
  })

  it('shows the disabled-default warning when the configured default casing differs from the disabled id', async () => {
    const user = userEvent.setup()
    currentDefault = 'MOHIST/LOCAL'
    currentDisabledIds = ['mohist/local']

    renderSection()

    await waitFor(() =>
      expect(screen.getByTestId('project-default-workflow-disabled-warning')).toHaveTextContent(
        'MOHIST/LOCAL',
      ),
    )
    expect(screen.queryByTestId('project-default-workflow-orphan-warning')).not.toBeInTheDocument()

    await user.click(screen.getByTestId('project-default-workflow-select'))

    const disabledOption = await screen.findByRole('option', { name: /Mohist Local/ })
    expect(disabledOption).toHaveAttribute('aria-disabled', 'true')
  })

  it('keeps workflow switches inactive until the project blacklist has loaded', async () => {
    let releaseResponse!: () => void
    const response = new Promise<void>((resolve) => {
      releaseResponse = resolve
    })
    server.use(
      http.get('/api/projects/test-project/workflow-profile/default', async () => {
        await response
        return HttpResponse.json({
          success: true,
          data: { projectId: 'test-project', defaultWorkflowProfileId: null, disabledWorkflowProfileIds: ['mohist/local'] },
        })
      }),
    )

    renderSection()

    expect(screen.getByRole('status')).toBeInTheDocument()
    expect(screen.queryByRole('switch', { name: /Disable workflow profile Mohist Local/ })).not.toBeInTheDocument()

    releaseResponse()

    expect(await screen.findByRole('switch', { name: /Enable workflow profile Mohist Local/ })).toBeInTheDocument()
  })

  it('shows an error instead of defaulting switches to enabled when the blacklist fails to load', async () => {
    server.use(
      http.get('/api/projects/test-project/workflow-profile/default', () =>
        HttpResponse.json({ success: false, error: 'boom' }, { status: 500 }),
      ),
    )

    renderSection()

    await waitFor(() =>
      expect(screen.getByText('Failed to load workflow profile settings.')).toBeInTheDocument(),
    )
    expect(screen.queryByRole('switch')).not.toBeInTheDocument()
  })

  it('renders accessible switches that write enable and disable mutations', async () => {
    const user = userEvent.setup()
    currentDisabledIds = ['mohist/github-pr']

    renderSection()

    const disabledSwitch = await screen.findByRole('switch', { name: /Enable workflow profile Mohist GitHub PR/ })
    await user.click(disabledSwitch)

    await waitFor(() =>
      expect(requests).toContainEqual(expect.objectContaining({
        method: 'POST',
        body: { profileId: 'mohist/github-pr' },
      })),
    )
  })

  it('blocks disabling the only enabled workflow inline', async () => {
    const user = userEvent.setup()
    currentDisabledIds = ['mohist/local', 'mohist/quick-fix']

    renderSection()

    const onlyEnabledSwitch = await screen.findByRole('switch', { name: /Disable workflow profile Mohist GitHub PR/ })
    await user.click(onlyEnabledSwitch)

    expect(await screen.findByTestId('workflow-profile-mohist/github-pr-blocked')).toHaveTextContent(
      'At least one workflow profile must remain enabled.',
    )
    expect(requests.filter((request) => request.url.includes('/workflow-profile/disable'))).toHaveLength(0)
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
