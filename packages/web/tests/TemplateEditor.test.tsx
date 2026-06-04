// @vitest-environment jsdom
import {
  afterAll,
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest'
import { fireEvent, render, screen, waitFor, within } from './test-utils'
import {
  TemplateEditor,
  type TemplateEditorTarget,
} from '../src/pages/settings/ui/TemplateEditor'
import type { ProjectTemplate } from '../src/entities/template'
import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'

const PROJECT_ID = 'test-project'

const SYSTEM_TARGET: ProjectTemplate = {
  key: 'proposal',
  displayName: 'Generate Proposal',
  description: 'Creates the OpenSpec proposal.md for an issue',
  tags: ['plan', 'openspec'],
  stage: 'plan',
  body: 'proposal body for ${{ issue.number }} and ${{ project.id }} and ${{ unknownVar }}',
  source: 'system',
}

const SYSTEM_TARGET_EDITOR: TemplateEditorTarget = {
  mode: 'override',
  template: SYSTEM_TARGET,
  initialBody: SYSTEM_TARGET.body,
  initialDisplayName: SYSTEM_TARGET.displayName,
  initialDescription: SYSTEM_TARGET.description,
  initialTags: SYSTEM_TARGET.tags,
  initialStage: SYSTEM_TARGET.stage,
}

const handlers = [
  http.post(
    `/api/projects/${PROJECT_ID}/templates/:key/override`,
    async ({ request }) => {
      await request.json()
      return HttpResponse.json({
        success: true,
        data: {
          projectId: PROJECT_ID,
          key: 'proposal',
          displayName: 'Updated Proposal',
          description: 'Updated',
          tags: ['plan'],
          stage: 'plan',
          body: 'updated body',
          updatedAt: '2024-01-01T00:00:00.000Z',
        },
      })
    },
  ),
  http.post(
    `/api/projects/${PROJECT_ID}/templates/:key/preview`,
    async ({ request }) => {
      const body = (await request.json()) as { variables: Record<string, unknown> }
      const issue = (body.variables.issue as { number?: unknown } | undefined) ?? {}
      const project = (body.variables.project as { id?: unknown } | undefined) ?? {}
      const rendered = `proposal body for ${issue.number ?? '<missing>'} and ${
        project.id ?? '<missing>'
      } and <missing>`
      return HttpResponse.json({
        success: true,
        data: {
          rendered,
          missingVariables: ['unknownVar'],
          depth: 1,
        },
      })
    },
  ),
  http.post('/api/templates/extract-variables', async ({ request }) => {
    const body = (await request.json()) as { body: string }
    const matches = body.body.match(/\$\{\{\s*([\w.]+)\s*\}\}/g) ?? []
    const variables = Array.from(
      new Set(
        matches.map((m) => {
          const inner = m.replace(/^\$\{\{\s*/, '').replace(/\s*\}\}$/, '')
          return inner
        }),
      ),
    ).sort()
    return HttpResponse.json({ success: true, data: { variables } })
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
  server.resetHandlers(...handlers)
})

afterEach(() => {
  server.resetHandlers(...handlers)
  vi.restoreAllMocks()
})

describe('TemplateEditor', () => {
  it('opens with all metadata fields populated from the target', () => {
    render(
      <TemplateEditor
        projectId={PROJECT_ID}
        target={SYSTEM_TARGET_EDITOR}
        onClose={vi.fn()}
      />,
    )

    const editor = screen.getByTestId('template-editor')
    expect(within(editor).getByTestId('template-editor-key')).toHaveValue('proposal')
    expect(within(editor).getByTestId('template-editor-key')).toBeDisabled()
    expect(within(editor).getByTestId('template-editor-displayname')).toHaveValue(
      'Generate Proposal',
    )
    expect(within(editor).getByTestId('template-editor-description')).toHaveValue(
      'Creates the OpenSpec proposal.md for an issue',
    )
    expect(within(editor).getByTestId('template-editor-tags')).toHaveValue('plan, openspec')
    expect(within(editor).getByTestId('template-editor-stage')).toHaveValue('plan')
    expect(within(editor).getByTestId('template-editor-body')).toHaveValue(
      SYSTEM_TARGET.body,
    )
  })

  it('renders the preview and lists referenced variables with availability indicators', async () => {
    render(
      <TemplateEditor
        projectId={PROJECT_ID}
        target={SYSTEM_TARGET_EDITOR}
        onClose={vi.fn()}
      />,
    )

    const previewPane = await screen.findByTestId('template-editor-preview')
    await waitFor(() => {
      expect(previewPane.textContent).toContain('proposal body for 1 and demo-project')
    })

    const variablesList = screen.getByTestId('template-editor-variables')
    expect(within(variablesList).getByTestId('template-editor-variable-issue.number')).toHaveAttribute(
      'data-available',
      'yes',
    )
    expect(within(variablesList).getByTestId('template-editor-variable-project.id')).toHaveAttribute(
      'data-available',
      'yes',
    )
    expect(within(variablesList).getByTestId('template-editor-variable-unknownVar')).toHaveAttribute(
      'data-available',
      'no',
    )
  })

  it('Save sends PUT and closes the editor on success', async () => {
    const onClose = vi.fn()
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
              displayName: 'Generate Proposal',
              description: 'Creates the OpenSpec proposal.md for an issue',
              tags: ['plan', 'openspec'],
              stage: 'plan',
              body: 'updated body',
              updatedAt: '2024-01-01T00:00:00.000Z',
            },
          })
        },
      ),
    )

    render(
      <TemplateEditor
        projectId={PROJECT_ID}
        target={SYSTEM_TARGET_EDITOR}
        onClose={onClose}
      />,
    )

    const editor = screen.getByTestId('template-editor')
    fireEvent.change(within(editor).getByTestId('template-editor-body'), {
      target: { value: 'updated body' },
    })
    fireEvent.click(within(editor).getByTestId('template-editor-save'))

    await waitFor(() => {
      expect(putKey).toBe('proposal')
    })
    expect(putPayload).toMatchObject({ body: 'updated body' })
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('Cancel closes the editor without sending a PUT', async () => {
    const onClose = vi.fn()
    const putSpy = vi.fn()
    server.use(
      http.put(
        `/api/projects/${PROJECT_ID}/templates/:key/override`,
        async ({ request }) => {
          putSpy(await request.json())
          return HttpResponse.json({ success: true, data: {} })
        },
      ),
    )

    render(
      <TemplateEditor
        projectId={PROJECT_ID}
        target={SYSTEM_TARGET_EDITOR}
        onClose={onClose}
      />,
    )

    fireEvent.click(screen.getByTestId('template-editor-cancel'))

    await waitFor(() => expect(onClose).toHaveBeenCalled())
    expect(putSpy).not.toHaveBeenCalled()
  })

  it('Reset reverts the form to the original values without sending a PUT', async () => {
    const onClose = vi.fn()
    const putSpy = vi.fn()
    server.use(
      http.put(
        `/api/projects/${PROJECT_ID}/templates/:key/override`,
        async ({ request }) => {
          putSpy(await request.json())
          return HttpResponse.json({ success: true, data: {} })
        },
      ),
    )

    render(
      <TemplateEditor
        projectId={PROJECT_ID}
        target={SYSTEM_TARGET_EDITOR}
        onClose={onClose}
      />,
    )

    const editor = screen.getByTestId('template-editor')
    const bodyField = within(editor).getByTestId(
      'template-editor-body',
    ) as HTMLTextAreaElement
    const displayNameField = within(editor).getByTestId(
      'template-editor-displayname',
    ) as HTMLInputElement

    fireEvent.change(bodyField, { target: { value: 'scratch edit' } })
    fireEvent.change(displayNameField, { target: { value: 'Scratch' } })

    fireEvent.click(within(editor).getByTestId('template-editor-reset'))

    expect(bodyField.value).toBe(SYSTEM_TARGET.body)
    expect(displayNameField.value).toBe('Generate Proposal')
    expect(putSpy).not.toHaveBeenCalled()
    expect(onClose).not.toHaveBeenCalled()
  })
})
