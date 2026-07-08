import type { QueryClient } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { http, HttpResponse } from 'msw'
import { server, useMswServer } from '../../../../tests/support/msw'
import { approvalWaitQueryOptions, invalidateApprovalWait } from './approval-wait'

const APPROVAL_WAIT_DTO = {
  window: { from: '2026-06-20T00:00:00+00:00', to: '2026-06-27T00:00:00+00:00' },
  sampleCount: 1,
  averageSeconds: 11_520,
  medianSeconds: 11_520,
  maxSeconds: 11_520,
}

function recordApprovalWaitRequests() {
  const urls: string[] = []
  server.use(
    http.get('*/api/projects/:projectId/issues/metrics/approval-wait', ({ request }) => {
      const url = new URL(request.url)
      urls.push(url.pathname + url.search)
      return HttpResponse.json({ success: true, data: APPROVAL_WAIT_DTO })
    }),
  )
  return urls
}

useMswServer()

describe('approvalWaitQueryOptions', () => {
  it('uses the query key ["issues","metrics","approval-wait", projectId]', () => {
    expect(approvalWaitQueryOptions('proj-1').queryKey).toEqual(['issues', 'metrics', 'approval-wait', 'proj-1'])
  })

  it('scopes the query key to the given projectId', () => {
    expect(approvalWaitQueryOptions('proj-other').queryKey).toEqual(['issues', 'metrics', 'approval-wait', 'proj-other'])
  })

  it('fetches the project approval-wait metrics endpoint', async () => {
    const urls = recordApprovalWaitRequests()

    const data = await approvalWaitQueryOptions('proj-1').queryFn()

    expect(urls).toEqual(['/api/projects/proj-1/issues/metrics/approval-wait'])
    expect(data.sampleCount).toBe(1)
    expect(data.averageSeconds).toBe(11_520)
  })

  it('uses a 60 second staleTime', () => {
    expect(approvalWaitQueryOptions('proj-1').staleTime).toBe(60_000)
  })

  it('is disabled when projectId is missing', () => {
    expect(approvalWaitQueryOptions(null).enabled).toBe(false)
  })

  it('is enabled when projectId is set', () => {
    expect(approvalWaitQueryOptions('proj-1').enabled).toBe(true)
  })
})

describe('invalidateApprovalWait', () => {
  it('invalidates the shared approval-wait query prefix', () => {
    const queryClient = {
      invalidateQueries: vi.fn(),
    } as unknown as QueryClient

    invalidateApprovalWait(queryClient)

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: ['issues', 'metrics', 'approval-wait'],
    })
  })
})
