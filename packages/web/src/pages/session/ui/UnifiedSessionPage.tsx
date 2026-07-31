import {
  useUnifiedSessionDataSource,
  type UnifiedSessionDataSourceDependencies,
} from '../data/useUnifiedSessionDataSource'
import {
  SessionDetailShell,
  type SessionDetailShellComponents,
} from './SessionDetailShell'

export interface UnifiedSessionPageDependencies {
  dataSource?: Partial<UnifiedSessionDataSourceDependencies>
  shellComponents?: Partial<SessionDetailShellComponents>
}

export function UnifiedSessionPage({ dependencies }: { dependencies?: UnifiedSessionPageDependencies } = {}) {
  const data = useUnifiedSessionDataSource(dependencies?.dataSource)
  return <SessionDetailShell data={data} components={dependencies?.shellComponents} />
}
