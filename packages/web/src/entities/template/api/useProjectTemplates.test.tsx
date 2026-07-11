import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { useProjectTemplates, type ProjectTemplate, type ProjectTemplatesFetcher } from '..'

const PROJECT_ID = 'test-project'
const OTHER_PROJECT_ID = 'other-project'

const BASE_TEMPLATES = [
  {
    key: 'proposal',
    displayName: 'Generate Proposal',
    description: 'Creates the OpenSpec proposal.md for an issue',
    tags: ['plan', 'openspec'],
    stage: 'plan',
    body: 'system proposal body',
    source: 'system' as const,
  },
  {
    key: 'build',
    displayName: 'Build Task',
    description: 'Implements a single build task',
    tags: ['build'],
    stage: 'build',
    body: 'system build body',
    source: 'system' as const,
  },
]

let templatesResponse: ProjectTemplate[] = BASE_TEMPLATES
const fetchCalls: string[] = []

const fetcher: ProjectTemplatesFetcher = async (projectId) => {
  fetchCalls.push(projectId)
  return templatesResponse
}

const queryClients: QueryClient[] = []

function createQueryClient() {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
  queryClients.push(qc)
  return qc
}

function renderUseProjectTemplates(projectId: string | undefined) {
  const queryClient = createQueryClient()
  return renderHook(() => useProjectTemplates(projectId, fetcher), {
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  })
}

beforeEach(() => {
  templatesResponse = BASE_TEMPLATES
  fetchCalls.length = 0
})

afterEach(() => {
  for (const qc of queryClients) qc.clear()
  queryClients.length = 0
})

describe('useProjectTemplates hook', () => {
  it('calls the project templates client and returns the effective templates', async () => {
    const { result } = renderUseProjectTemplates(PROJECT_ID)

    await waitFor(() => {
      expect(result.current.data).toEqual(BASE_TEMPLATES)
    })

    expect(fetchCalls).toEqual([PROJECT_ID])
  })

  it('returns templates with the source field preserved', async () => {
    const mixed = [
      { ...BASE_TEMPLATES[0], source: 'project-override' as const },
      { ...BASE_TEMPLATES[1], source: 'system' as const },
      {
        key: 'deploy-checklist',
        displayName: 'Deploy Checklist',
        description: 'project-unique',
        tags: ['deploy'],
        stage: 'check',
        body: 'deploy body',
        source: 'project-new' as const,
      },
    ]
    templatesResponse = mixed

    const { result } = renderUseProjectTemplates(PROJECT_ID)

    await waitFor(() => {
      expect(result.current.data).toHaveLength(3)
    })

    const byKey = Object.fromEntries(
      result.current.data!.map((t: { key: string; source: string }) => [t.key, t]),
    )
    expect(byKey.proposal!.source).toBe('project-override')
    expect(byKey.build!.source).toBe('system')
    expect(byKey['deploy-checklist']!.source).toBe('project-new')
  })

  it('does not fetch when projectId is undefined', () => {
    const { result } = renderUseProjectTemplates(undefined)

    expect(result.current.isLoading).toBe(false)
    expect(result.current.data).toBeUndefined()
    expect(fetchCalls).toEqual([])
  })

  it('scopes the fetch to the provided projectId', async () => {
    templatesResponse = []

    const { result } = renderUseProjectTemplates(OTHER_PROJECT_ID)

    await waitFor(() => {
      expect(result.current.isSuccess).toBe(true)
    })

    expect(fetchCalls).toEqual([OTHER_PROJECT_ID])
  })
})
