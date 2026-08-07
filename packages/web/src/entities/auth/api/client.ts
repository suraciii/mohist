import { request } from '../../../shared/api/client'

export function createSession(token: string) {
  return request<null>('/auth/session', {
    method: 'POST',
    body: JSON.stringify({ token }),
  })
}

export function getSessionStatus() {
  return request<null>('/auth/session').then(() => true)
}

export function deleteSession() {
  return request<null>('/auth/session', {
    method: 'DELETE',
  })
}
