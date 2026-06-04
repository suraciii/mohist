// @vitest-environment jsdom
import {
  afterAll,
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
} from 'vitest'
import { fireEvent, render, screen, waitFor, within } from './test-utils'
import { TemplatesSection } from '../src/pages/settings/ui/TemplatesSection'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'

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
    body: 'system proposal body ${{ openspecChangeDir }}',
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
  http.delete(
    `/api/projects/${PROJECT_ID}/templates/:key/override`,
    ({ params }) =>
      HttpResponse.json({
        success: true,
        data: { message: `Override ${params.key} removed` },
      }),
  ),
]

const server = setupServer(...handlers)

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' })
})

afterAll(() => {
  server.close()
})

beforeEach(() => {
  server.resetHandlers(...handlers)
})

afterEach(() => {
  server.resetHandlers(...handlers)
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
    it('sends DELETE to remove the override for an overridden row', async () => {
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

      await waitFor(() => expect(deletedKey).toBe('proposal'))
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
})
