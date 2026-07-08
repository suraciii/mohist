import { describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { issueTemplateQueryOptions, issueTemplatesQueryOptions } from './queries'

const TEMPLATES_DTO = [
  { id: 'feature', name: 'Feature', description: '', source: 'builtin' },
]

const TEMPLATE_DTO = {
  id: 'feature',
  name: 'Feature',
  description: '',
  body: '',
  source: 'builtin',
}

function recordIssueTemplatesRequests() {
  const urls: string[] = []
  server.use(
    http.get('*/api/issue-templates', ({ request }) => {
      const url = new URL(request.url)
      urls.push(url.pathname + url.search)
      return HttpResponse.json({ success: true, data: TEMPLATES_DTO })
    }),
  )
  return urls
}

function recordIssueTemplateRequests() {
  const urls: string[] = []
  server.use(
    http.get('*/api/issue-templates/mohist/default', ({ request }) => {
      const url = new URL(request.url)
      urls.push(url.pathname + url.search)
      return HttpResponse.json({ success: true, data: TEMPLATE_DTO })
    }),
  )
  return urls
}

useMswServer()

describe('issueTemplatesQueryOptions', () => {
  it('uses a project-scoped query key', () => {
    expect(issueTemplatesQueryOptions('proj-1').queryKey).toEqual(['issue-templates', 'proj-1'])
  })

  it('fetches the issue templates endpoint scoped to the projectId', async () => {
    const urls = recordIssueTemplatesRequests()

    const data = await issueTemplatesQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/issue-templates?projectId=proj-1'])
    expect(data).toEqual(TEMPLATES_DTO)
  })

  it('is disabled when projectId is missing', () => {
    expect(issueTemplatesQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(issueTemplatesQueryOptions('proj-1').enabled).toBe(true)
  })
})

describe('issueTemplateQueryOptions', () => {
  it('uses a query key keyed on (projectId, name)', () => {
    expect(issueTemplateQueryOptions('proj-1', 'mohist/default').queryKey).toEqual(['issue-template', 'proj-1', 'mohist/default'])
  })

  it('fetches the issue template endpoint for (name, projectId)', async () => {
    const urls = recordIssueTemplateRequests()

    const data = await issueTemplateQueryOptions('proj-1', 'mohist/default').queryFn()

    expect(urls).toEqual(['/api/issue-templates/mohist/default?projectId=proj-1'])
    expect(data).toEqual(TEMPLATE_DTO)
  })

  it('is disabled when projectId is missing', () => {
    expect(issueTemplateQueryOptions(null, 'mohist/default').enabled).toBe(false)
  })

  it('is disabled when name is null', () => {
    expect(issueTemplateQueryOptions('proj-1', null).enabled).toBe(false)
  })

  it('is enabled when both projectId and name are set', () => {
    expect(issueTemplateQueryOptions('proj-1', 'mohist/default').enabled).toBe(true)
  })
})
