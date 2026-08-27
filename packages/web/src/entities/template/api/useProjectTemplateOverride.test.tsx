import { beforeEach, describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { projectTemplateOverrideQueryOptions } from '..'
import { useMswServer } from '../../../../tests/support/msw'

const PROJECT_ID = 'test-project'
const KEY = 'plan'

const OVERRIDE_ROW = {
  projectId: PROJECT_ID,
  key: KEY,
  displayName: 'Plan Change',
  description: 'project override description',
  tags: ['plan'],
  stage: 'plan',
  body: 'project override body',
  updatedAt: '2024-01-01T00:00:00.000Z',
}

let overrideResponse: {
  success: boolean
  data?: typeof OVERRIDE_ROW
  error?: string
  code?: string
} = { success: true, data: OVERRIDE_ROW }
let overrideStatus = 200
const requestedUrls: string[] = []

const overrideHandler = vi.fn(({ request }: { request: Request }) => {
  requestedUrls.push(request.url)
  return HttpResponse.json(overrideResponse, { status: overrideStatus })
})

useMswServer(http.get('*/api/projects/:projectId/templates/:key/override', overrideHandler))

beforeEach(() => {
  overrideResponse = { success: true, data: OVERRIDE_ROW }
  overrideStatus = 200
  requestedUrls.length = 0
  overrideHandler.mockClear()
})

describe('projectTemplateOverrideQueryOptions', () => {
  it('fetches the project-scoped override and returns the row', async () => {
    const result = await projectTemplateOverrideQueryOptions(PROJECT_ID, KEY).queryFn()

    expect(result).toEqual(OVERRIDE_ROW)
    expect(overrideHandler).toHaveBeenCalledTimes(1)
    expect(new URL(requestedUrls[0]!).pathname).toBe(`/api/projects/${PROJECT_ID}/templates/${KEY}/override`)
  })

  it('does not retry a 404 and surfaces the API error', async () => {
    overrideResponse = { success: false, error: 'No override', code: 'not_found' }
    overrideStatus = 404
    const options = projectTemplateOverrideQueryOptions(PROJECT_ID, KEY)

    await expect(options.queryFn()).rejects.toMatchObject({ status: 404 })
    expect(options.retry(0, { status: 404 })).toBe(false)
    expect(options.retry(0, { status: 500 })).toBe(true)
    expect(options.retry(1, { status: 500 })).toBe(false)
  })

  it('is disabled when either projectId or key is missing', () => {
    expect(projectTemplateOverrideQueryOptions(PROJECT_ID, undefined).enabled).toBe(false)
    expect(projectTemplateOverrideQueryOptions(undefined, KEY).enabled).toBe(false)
    expect(projectTemplateOverrideQueryOptions(PROJECT_ID, KEY).enabled).toBe(true)
  })

  it('scopes the fetch to the provided key', async () => {
    const customKey = 'custom-key'
    overrideResponse = { success: true, data: { ...OVERRIDE_ROW, key: customKey } }

    await projectTemplateOverrideQueryOptions(PROJECT_ID, customKey).queryFn()

    expect(new URL(requestedUrls[0]!).pathname).toBe(`/api/projects/${PROJECT_ID}/templates/${customKey}/override`)
  })
})
