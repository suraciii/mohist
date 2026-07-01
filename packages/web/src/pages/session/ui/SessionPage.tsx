import { useIssueSessionDataSource } from '../data/useIssueSessionDataSource'
import { SessionDetailShell } from './SessionDetailShell'

export function SessionPage() {
  const data = useIssueSessionDataSource()
  return <SessionDetailShell data={data} />
}
