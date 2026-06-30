import { useIssueSessionDataSource } from '../data/useIssueSessionDataSource'
import { SessionDetailShell } from './SessionDetailShell'

export function SessionPage() {
  const data = useIssueSessionDataSource()
  return <SessionDetailShell data={data} />
}

export function isCurrentSiblingSession(
  sibling: { id: string; sessionName: string },
  currentKey: string | null,
): boolean {
  return sibling.sessionName === currentKey || sibling.id === currentKey
}
