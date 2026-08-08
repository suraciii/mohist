import { beforeEach, describe, expect, it } from 'vitest'
import { http, HttpResponse } from 'msw'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from './test-utils'
import { useMswServer } from './support/msw'
import { DevicePage } from '../src/pages/device'

/**
 * RFC 8628 confirmation page (docs/auth.md "远程 CLI：设备授权登录"):
 * typing a code resolves the pending authorization (case and hyphens
 * ignored) and the logged-in user can approve or deny; the server
 * enforces the Web session on top of AuthGate.
 */
describe('DevicePage', () => {
  const verifyRequests: string[] = []
  const decisionRequests: { flowId: string; decision: string }[] = []

  beforeEach(() => {
    verifyRequests.length = 0
    decisionRequests.length = 0
  })

  useMswServer(
    http.post('*/api/auth/device/verify', async ({ request }) => {
      const body = (await request.json()) as { userCode: string }
      verifyRequests.push(body.userCode)
      if (body.userCode === 'ABCDEFGH') {
        return HttpResponse.json({
          success: true,
          data: { flowId: 'device_flow_1', clientName: 'my-laptop', expiresAt: '2026-01-01T00:10:00+00:00' },
        })
      }
      return HttpResponse.json({ success: false, error: 'Code not found.', code: 'device_code_not_found' }, { status: 404 })
    }),
    http.post('*/api/auth/device/decision', async ({ request }) => {
      const body = (await request.json()) as { flowId: string; decision: string }
      decisionRequests.push(body)
      return HttpResponse.json({ success: true, data: { status: body.decision } })
    }),
  )

  it('resolves a typed code and approves the authorization', async () => {
    render(<DevicePage />)

    const input = screen.getByLabelText('Confirmation code')
    // Case and hyphens are ignored (XXXX-XXXX grouped form).
    fireEvent.change(input, { target: { value: 'abcd-efgh' } })

    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await waitFor(() => expect(screen.getByTestId('device-confirm')).toBeInTheDocument())
    expect(screen.getByText('my-laptop', { exact: false })).toBeInTheDocument()
    expect(verifyRequests).toEqual(['ABCDEFGH'])

    fireEvent.click(screen.getByRole('button', { name: 'Approve' }))
    await waitFor(() => expect(screen.getByTestId('device-approved')).toBeInTheDocument())
    expect(decisionRequests).toEqual([{ flowId: 'device_flow_1', decision: 'approved' }])
  })

  it('denies the authorization', async () => {
    render(<DevicePage />)

    fireEvent.change(screen.getByLabelText('Confirmation code'), { target: { value: 'ABCDEFGH' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))
    await waitFor(() => screen.getByTestId('device-confirm'))

    fireEvent.click(screen.getByRole('button', { name: 'Deny' }))
    await waitFor(() => expect(screen.getByTestId('device-denied')).toBeInTheDocument())
    expect(decisionRequests).toEqual([{ flowId: 'device_flow_1', decision: 'denied' }])
  })

  it('reports an unknown code', async () => {
    render(<DevicePage />)

    fireEvent.change(screen.getByLabelText('Confirmation code'), { target: { value: 'ZZZZZZZZ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Continue' }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Code not found'))
    expect(screen.queryByTestId('device-confirm')).not.toBeInTheDocument()
  })
})
