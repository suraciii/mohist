import {
  useGenericSessionDataSource,
  type GenericSessionDataSourceDependencies,
} from '../data/useGenericSessionDataSource'
import {
  SessionDetailShell,
  type SessionDetailShellComponents,
} from './SessionDetailShell'

export interface GenericSessionPageDependencies {
  dataSource?: GenericSessionDataSourceDependencies
  shellComponents?: Partial<SessionDetailShellComponents>
}

export function GenericSessionPage({ dependencies }: { dependencies?: GenericSessionPageDependencies } = {}) {
  const data = useGenericSessionDataSource(dependencies?.dataSource)
  return <SessionDetailShell data={data} components={dependencies?.shellComponents} />
}
