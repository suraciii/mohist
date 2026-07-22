import {
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from 'vitest'
import { fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import {
  TemplateEditor,
  type TemplateEditorHooks,
  type TemplateEditorTarget,
} from './TemplateEditor'
import type { ProjectTemplate } from '../../../entities/template'
import { useMutation } from '@tanstack/react-query'

const PROJECT_ID = 'test-project'

const SYSTEM_TARGET: ProjectTemplate = {
  key: 'proposal',
  displayName: 'Generate Proposal',
  description: 'Creates the OpenSpec proposal.md for an issue',
  tags: ['plan', 'openspec'],
  stage: 'plan',
  body: 'proposal body for ${{ issue.number }} and ${{ issue.projectId }} and ${{ vars.unknownVar }}',
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

let upsertCalls: Array<{ key: string; payload: Record<string, unknown> }> = []

const testHooks = {
  useUpsert: () => useMutation({
    mutationFn: async ({ key, payload }: { key: string; payload: Record<string, unknown> }) => {
      upsertCalls.push({ key, payload })
      return {
        projectId: PROJECT_ID,
        key,
        displayName: String(payload.displayName),
        description: String(payload.description),
        tags: payload.tags as string[],
        stage: payload.stage as string | null,
        body: String(payload.body),
        updatedAt: '2024-01-01T00:00:00.000Z',
      }
    },
  }),
  usePreview: () => useMutation({
    mutationFn: async ({ variables }: { variables: Record<string, unknown> }) => {
      const issue = (variables.issue as { number?: unknown } | undefined) ?? {}
      const issueProject = (variables.issue as { projectId?: unknown } | undefined) ?? {}
      return {
        rendered: `proposal body for ${issue.number ?? '<missing>'} and ${issueProject.projectId ?? '<missing>'} and <missing>`,
        missingVariables: ['vars.unknownVar'],
        depth: 1,
      }
    },
  }),
  useExtract: () => useMutation({
    mutationFn: async ({ body }: { body: string }) => {
      const matches = body.match(/\$\{\{\s*([\w.]+)\s*\}\}/g) ?? []
      const variables = Array.from(
        new Set(
          matches.map((m) => {
            const inner = m.replace(/^\$\{\{\s*/, '').replace(/\s*\}\}$/, '')
            return inner
          }),
        ),
      ).sort()
      return { variables }
    },
  }),
} as unknown as TemplateEditorHooks

function renderEditor(onClose = vi.fn()) {
  return render(
    <TemplateEditor
      projectId={PROJECT_ID}
      target={SYSTEM_TARGET_EDITOR}
      onClose={onClose}
      hooks={testHooks}
    />,
  )
}

beforeEach(() => {
  vi.restoreAllMocks()
  upsertCalls = []
})

describe('TemplateEditor', () => {
  it('opens with all metadata fields populated from the target', () => {
    renderEditor()

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
    renderEditor()

    const previewPane = await screen.findByTestId('template-editor-preview')
    await waitFor(() => {
      expect(previewPane.textContent).toContain('proposal body for 1 and demo-project')
    })

    const variablesList = screen.getByTestId('template-editor-variables')
    expect(within(variablesList).getByTestId('template-editor-variable-issue.number')).toHaveAttribute(
      'data-available',
      'yes',
    )
    expect(within(variablesList).getByTestId('template-editor-variable-issue.projectId')).toHaveAttribute(
      'data-available',
      'yes',
    )
    expect(within(variablesList).getByTestId('template-editor-variable-vars.unknownVar')).toHaveAttribute(
      'data-available',
      'no',
    )
  })

  it('Save sends the override payload and closes the editor on success', async () => {
    const onClose = vi.fn()
    renderEditor(onClose)

    const editor = screen.getByTestId('template-editor')
    fireEvent.change(within(editor).getByTestId('template-editor-body'), {
      target: { value: 'updated body' },
    })
    fireEvent.click(within(editor).getByTestId('template-editor-save'))

    await waitFor(() => {
      expect(upsertCalls).toHaveLength(1)
    })
    expect(upsertCalls[0]).toMatchObject({
      key: 'proposal',
      payload: { body: 'updated body' },
    })
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  })

  it('Cancel closes the editor without saving', async () => {
    const onClose = vi.fn()
    renderEditor(onClose)

    fireEvent.click(screen.getByTestId('template-editor-cancel'))

    await waitFor(() => expect(onClose).toHaveBeenCalled())
    expect(upsertCalls).toEqual([])
  })

  it('Reset reverts the form to the original values without saving', async () => {
    const onClose = vi.fn()
    renderEditor(onClose)

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
    expect(upsertCalls).toEqual([])
    expect(onClose).not.toHaveBeenCalled()
  })
})
