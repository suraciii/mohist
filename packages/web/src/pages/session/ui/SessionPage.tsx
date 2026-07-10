import {
  useIssueSessionDataSource,
  type IssueSessionDataSourceDependencies,
} from '../data/useIssueSessionDataSource'
import {
  SessionDetailShell,
  type SessionDetailShellComponents,
} from './SessionDetailShell'

export interface SessionPageDependencies {
  dataSource?: IssueSessionDataSourceDependencies
  shellComponents?: Partial<SessionDetailShellComponents>
}

export function SessionPage({ dependencies }: { dependencies?: SessionPageDependencies } = {}) {
  const data = useIssueSessionDataSource(dependencies?.dataSource)
  return <SessionDetailShell data={data} components={dependencies?.shellComponents} />
}
