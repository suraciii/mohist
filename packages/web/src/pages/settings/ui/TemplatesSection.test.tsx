import {
  beforeEach,
  describe,
  expect,
  it,
} from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import { TemplatesSection } from './TemplatesSection'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'

interface ProjectTemplate {
  key: string
  displayName: string
  description: string
  tags: string[]
  stage: string | null
  body: string
  source: 'system' | 'project-override' | 'project-new'
}

interface SystemTemplate {
  key: string
  displayName: string
  description: string
  tags: string[]
  stage: string | null
  body: string
}

const PROJECT_ID = 'test-project'

const SYSTEM_TEMPLATES: SystemTemplate[] = [
  {
    key: 'proposal',
    displayName: 'Generate Proposal',
    description: 'Creates the OpenSpec proposal.md for an issue',
    tags: ['plan', 'openspec'],
    stage: 'plan',
    body: 'system proposal body openspec/changes/issue-${{ issue.number }}',
  },
  {
    key: 'build',
    displayName: 'Build Task',
    description: 'Implements a single build task',
    tags: ['build'],
    stage: 'build',
    body: 'system build body',
  },
]

const EFFECTIVE_TEMPLATES: ProjectTemplate[] = [
  {
    key: 'proposal',
    displayName: 'Generate Proposal',
    description: 'Creates the OpenSpec proposal.md for an issue',
    tags: ['plan', 'openspec'],
    stage: 'plan',
    body: 'project override body',
    source: 'project-override',
  },
  {
    key: 'build',
    displayName: 'Build Task',
    description: 'Implements a single build task',
    tags: ['build'],
    stage: 'build',
    body: 'system build body',
    source: 'system',
  },
  {
    key: 'deploy-checklist',
    displayName: 'Deploy Checklist',
    description: 'Project-unique pre-deploy checklist',
    tags: ['deploy'],
    stage: 'check',
    body: 'deploy body',
    source: 'project-new',
  },
]

const handlers = [
  http.get('/api/templates/system', () =>
    HttpResponse.json({ success: true, data: SYSTEM_TEMPLATES }),
  ),
  http.get(`/api/projects/${PROJECT_ID}/templates`, () =>
    HttpResponse.json({ success: true, data: EFFECTIVE_TEMPLATES }),
  ),
  http.post('/api/templates/extract-variables', () =>
    HttpResponse.json({ success: true, data: { variables: [] } }),
  ),
  http.post(`/api/projects/${PROJECT_ID}/templates/:key/preview`, () =>
    HttpResponse.json({
      success: true,
      data: { rendered: 'Preview', missingVariables: [], depth: 0 },
    }),
  ),
  http.delete(
    `/api/projects/${PROJECT_ID}/templates/:key/override`,
    ({ params }) =>
      HttpResponse.json({
        success: true,
        data: { message: `Override ${params.key} removed` },
      }),
  ),
]

useMswServer(...handlers)

beforeEach(() => {
  server.use(...handlers)
})

describe('TemplatesSection', () => {
  describe('List view with mixed sources', () => {
    it('renders one row per effective template with the correct source label variant', async () => {
      render(<TemplatesSection />)

      for (const t of EFFECTIVE_TEMPLATES) {
        await waitFor(() =>
          expect(screen.getByTestId(`template-row-${t.key}`)).toBeInTheDocument(),
        )
      }

      const proposalRow = screen.getByTestId('template-row-proposal')
      expect(within(proposalRow).getByTestId('template-source-label')).toHaveTextContent(
        'projectⓘ',
      )

      const buildRow = screen.getByTestId('template-row-build')
      expect(within(buildRow).getByTestId('template-source-label')).toHaveTextContent('system')

      const newRow = screen.getByTestId('template-row-deploy-checklist')
      expect(within(newRow).getByTestId('template-source-label')).toHaveTextContent(
        'projectⓘ new',
      )
    })

    it('shows stage badges and tag chips on rows', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      const proposalRow = screen.getByTestId('template-row-proposal')
      expect(within(proposalRow).getByTestId('template-stage-badge')).toHaveTextContent('plan')
      expect(within(proposalRow).getAllByTestId('template-tag-chip')).toHaveLength(2)
    })

    it('shows only Override/Preview for system rows; Edit/Preview/Delete for project rows', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-build')).toBeInTheDocument(),
      )

      expect(screen.getByTestId('template-override-build')).toBeInTheDocument()
      expect(screen.getByTestId('template-preview-build')).toBeInTheDocument()
      expect(screen.queryByTestId('template-edit-build')).not.toBeInTheDocument()
      expect(screen.queryByTestId('template-delete-build')).not.toBeInTheDocument()

      expect(screen.queryByTestId('template-override-proposal')).not.toBeInTheDocument()
      expect(screen.getByTestId('template-edit-proposal')).toBeInTheDocument()
      expect(screen.getByTestId('template-preview-proposal')).toBeInTheDocument()
      expect(screen.getByTestId('template-reset-proposal')).toBeInTheDocument()
      expect(screen.getByTestId('template-delete-proposal')).toBeInTheDocument()
    })
  })

  describe('Search filtering', () => {
    it('exposes an accessible label for the search input', async () => {
      render(<TemplatesSection />)

      const search = await screen.findByLabelText('Search templates')
      expect(search).toBe(screen.getByTestId('template-search'))
    })

    it('filters rows by key when search matches', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.change(screen.getByTestId('template-search'), {
        target: { value: 'deploy' },
      })

      await waitFor(() => {
        expect(screen.getByTestId('template-row-deploy-checklist')).toBeInTheDocument()
      })
      expect(screen.queryByTestId('template-row-proposal')).not.toBeInTheDocument()
      expect(screen.queryByTestId('template-row-build')).not.toBeInTheDocument()
    })

    it('filters rows by tag when search matches a tag', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.change(screen.getByTestId('template-search'), {
        target: { value: 'openspec' },
      })

      await waitFor(() => {
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument()
      })
      expect(screen.queryByTestId('template-row-build')).not.toBeInTheDocument()
    })

    it('restores the full list when search is cleared', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.change(screen.getByTestId('template-search'), {
        target: { value: 'nope' },
      })
      await waitFor(() =>
        expect(screen.queryByTestId('template-row-proposal')).not.toBeInTheDocument(),
      )

      fireEvent.change(screen.getByTestId('template-search'), {
        target: { value: '' },
      })
      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )
    })
  })

  describe('Override action', () => {
    it('opens the editor with override mode and system body when Override is clicked', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-build')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-override-build'))

      const editor = await screen.findByTestId('template-editor')
      expect(editor).toBeInTheDocument()
      expect(within(editor).getByTestId('template-editor-key')).toHaveValue('build')
      expect(within(editor).getByTestId('template-editor-body')).toHaveValue(
        'system build body',
      )
    })
  })

  describe('Reset action', () => {
    it('sends DELETE to remove the override only after the shared AlertDialog is confirmed', async () => {
      let deletedKey: string | null = null
      server.use(
        http.delete(
          `/api/projects/${PROJECT_ID}/templates/:key/override`,
          ({ params }) => {
            deletedKey = String(params.key)
            return HttpResponse.json({
              success: true,
              data: { message: `Override ${params.key} removed` },
            })
          },
        ),
      )

      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-reset-proposal'))

      const dialog = await screen.findByTestId('template-destructive-alert')
      expect(dialog).toBeInTheDocument()
      expect(dialog).toHaveAttribute('data-tone', 'destructive')

      expect(deletedKey).toBeNull()

      fireEvent.click(screen.getByTestId('template-destructive-alert-cancel'))

      await waitFor(() =>
        expect(screen.queryByTestId('template-destructive-alert')).not.toBeInTheDocument(),
      )

      expect(deletedKey).toBeNull()

      fireEvent.click(screen.getByTestId('template-reset-proposal'))
      await screen.findByTestId('template-destructive-alert')
      fireEvent.click(screen.getByTestId('template-destructive-alert-confirm'))

      await waitFor(() => expect(deletedKey).toBe('proposal'))
    })

    it('does not invoke DELETE when the AlertDialog is cancelled', async () => {
      let deleteCalled = false
      server.use(
        http.delete(
          `/api/projects/${PROJECT_ID}/templates/:key/override`,
          () => {
            deleteCalled = true
            return HttpResponse.json({
              success: true,
              data: { message: 'deleted' },
            })
          },
        ),
      )

      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-reset-proposal'))
      await screen.findByTestId('template-destructive-alert')
      fireEvent.click(screen.getByTestId('template-destructive-alert-cancel'))

      await waitFor(() =>
        expect(screen.queryByTestId('template-destructive-alert')).not.toBeInTheDocument(),
      )

      expect(deleteCalled).toBe(false)
    })

    it('renders a single shared AlertDialog instance for the section, not per row', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )
      await waitFor(() =>
        expect(screen.getByTestId('template-row-deploy-checklist')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-reset-proposal'))
      const dialog = await screen.findByTestId('template-destructive-alert')
      expect(dialog).toBeInTheDocument()

      const allDialogs = document.querySelectorAll('[data-testid="template-destructive-alert"]')
      expect(allDialogs).toHaveLength(1)
    })
  })

  describe('Delete action', () => {
    it('sends DELETE for a project-unique template only after the AlertDialog is confirmed', async () => {
      let deletedKey: string | null = null
      server.use(
        http.delete(
          `/api/projects/${PROJECT_ID}/templates/:key/override`,
          ({ params }) => {
            deletedKey = String(params.key)
            return HttpResponse.json({
              success: true,
              data: { message: `Override ${params.key} removed` },
            })
          },
        ),
      )

      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-deploy-checklist')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-delete-deploy-checklist'))

      const dialog = await screen.findByTestId('template-destructive-alert')
      expect(dialog).toBeInTheDocument()
      expect(deletedKey).toBeNull()

      fireEvent.click(screen.getByTestId('template-destructive-alert-confirm'))

      await waitFor(() => expect(deletedKey).toBe('deploy-checklist'))
    })

    it('does not invoke DELETE when the AlertDialog is cancelled on a project-unique template', async () => {
      let deleteCalled = false
      server.use(
        http.delete(
          `/api/projects/${PROJECT_ID}/templates/:key/override`,
          () => {
            deleteCalled = true
            return HttpResponse.json({
              success: true,
              data: { message: 'deleted' },
            })
          },
        ),
      )

      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-deploy-checklist')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-delete-deploy-checklist'))
      await screen.findByTestId('template-destructive-alert')
      fireEvent.click(screen.getByTestId('template-destructive-alert-cancel'))

      await waitFor(() =>
        expect(screen.queryByTestId('template-destructive-alert')).not.toBeInTheDocument(),
      )

      expect(deleteCalled).toBe(false)
    })
  })

  describe('New Template dialog', () => {
    it('rejects submission with empty key and shows an error', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-new-button'))
      const dialog = await screen.findByTestId('new-template-dialog')
      expect(dialog).toBeInTheDocument()

      fireEvent.change(within(dialog).getByTestId('new-template-body'), {
        target: { value: 'body content' },
      })
      fireEvent.click(within(dialog).getByTestId('new-template-create'))

      expect(within(dialog).getByTestId('new-template-error')).toHaveTextContent(
        'Key is required',
      )
    })

    it('rejects submission with empty body and shows an error', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-new-button'))
      const dialog = await screen.findByTestId('new-template-dialog')

      fireEvent.change(within(dialog).getByTestId('new-template-key'), {
        target: { value: 'deploy-checklist' },
      })
      fireEvent.click(within(dialog).getByTestId('new-template-create'))

      expect(within(dialog).getByTestId('new-template-error')).toHaveTextContent(
        'Body is required',
      )
    })

    it('sends PUT and closes the dialog on successful submission', async () => {
      let putPayload: unknown = null
      let putKey: string | null = null
      server.use(
        http.put(
          `/api/projects/${PROJECT_ID}/templates/:key/override`,
          async ({ params, request }) => {
            putKey = String(params.key)
            putPayload = await request.json()
            return HttpResponse.json({
              success: true,
              data: {
                projectId: PROJECT_ID,
                key: putKey,
                displayName: 'Deploy Checklist',
                description: '',
                tags: [],
                stage: null,
                body: 'my new body',
                updatedAt: '2024-01-01T00:00:00.000Z',
              },
            })
          },
        ),
      )

      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('template-new-button'))
      const dialog = await screen.findByTestId('new-template-dialog')

      fireEvent.change(within(dialog).getByTestId('new-template-key'), {
        target: { value: 'deploy-checklist' },
      })
      fireEvent.change(within(dialog).getByTestId('new-template-displayname'), {
        target: { value: 'Deploy Checklist' },
      })
      fireEvent.change(within(dialog).getByTestId('new-template-body'), {
        target: { value: 'my new body' },
      })
      fireEvent.click(within(dialog).getByTestId('new-template-create'))

      await waitFor(() => {
        expect(putKey).toBe('deploy-checklist')
      })
      expect(putPayload).toMatchObject({
        body: 'my new body',
        displayName: 'Deploy Checklist',
      })

      await waitFor(() =>
        expect(screen.queryByTestId('new-template-dialog')).not.toBeInTheDocument(),
      )
    })
  })

  describe('Empty list CTA', () => {
    it('renders an inline New Template action when there are no project templates', async () => {
      server.use(
        http.get(`/api/projects/${PROJECT_ID}/templates`, () =>
          HttpResponse.json({ success: true, data: [] }),
        ),
      )

      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('templates-empty-new-button')).toBeInTheDocument(),
      )

      fireEvent.click(screen.getByTestId('templates-empty-new-button'))
      const dialog = await screen.findByTestId('new-template-dialog')
      expect(dialog).toBeInTheDocument()
    })

    it('does not render the inline New Template action when the search filter is the only reason the list is empty', async () => {
      render(<TemplatesSection />)

      await waitFor(() =>
        expect(screen.getByTestId('template-row-proposal')).toBeInTheDocument(),
      )

      fireEvent.change(screen.getByTestId('template-search'), {
        target: { value: 'no-such-template' },
      })

      await waitFor(() =>
        expect(screen.queryByTestId('template-row-proposal')).not.toBeInTheDocument(),
      )
      expect(screen.queryByTestId('templates-empty-new-button')).not.toBeInTheDocument()
    })
  })

  describe('No-project state', () => {
    it('renders the no-project CTA (Select project + Create Project) when no project is selected', async () => {
      const { baseRender: renderRaw } = await import('../../../../tests/test-utils')
      const { ProjectProvider } = await import('../../../entities/project/model/ProjectContext')
      const { MemoryRouter } = await import('react-router-dom')
      const { QueryClient, QueryClientProvider } = await import('@tanstack/react-query')

      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

      renderRaw(
        <MemoryRouter>
          <QueryClientProvider client={queryClient}>
            <ProjectProvider initialProjectId={null} initialProjects={[]}>
              <TemplatesSection />
            </ProjectProvider>
          </QueryClientProvider>
        </MemoryRouter>,
      )

      expect(screen.getByTestId('no-project-select-button')).toBeInTheDocument()
      expect(screen.getByTestId('no-project-create-button')).toBeInTheDocument()
      expect(screen.queryByText('No project selected')).not.toBeInTheDocument()
    })
  })
})
