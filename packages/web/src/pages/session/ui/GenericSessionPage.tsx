import { useGenericSessionDataSource } from '../data/useGenericSessionDataSource'
import { SessionDetailShell } from './SessionDetailShell'

export function GenericSessionPage() {
  const data = useGenericSessionDataSource()
  return <SessionDetailShell data={data} />
}
