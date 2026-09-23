import { readFileSync, readdirSync, realpathSync, statSync } from 'node:fs'
import { isAbsolute, relative } from 'node:path'

function within(root: string, candidate: string): boolean {
  const child = relative(root, candidate)
  return child === '' || (child !== '..' && !child.startsWith('../') && !isAbsolute(child))
}

function disappeared(error: unknown): boolean {
  return error instanceof Error && 'code' in error && (error.code === 'ENOENT' || error.code === 'ESRCH')
}

export function inspectWorkspaceProcessUse(workspacePath: string): 'ready' | 'busy' | 'failed' {
  if (process.platform !== 'linux' || process.getuid === undefined) return 'failed'
  let root: string
  try {
    root = realpathSync(workspacePath)
  } catch (error) {
    return disappeared(error) ? 'ready' : 'failed'
  }
  let runnerCgroup: string
  try {
    runnerCgroup = readFileSync('/proc/self/cgroup', 'utf8')
  } catch {
    return 'failed'
  }

  try {
    for (const processEntry of readdirSync('/proc', { withFileTypes: true })) {
      if (!processEntry.isDirectory() || !/^\d+$/.test(processEntry.name)) continue
      const processPath = `/proc/${processEntry.name}`
      let uid: number
      try {
        uid = statSync(processPath).uid
      } catch (error) {
        if (disappeared(error)) continue
        return 'failed'
      }
      if (uid !== process.getuid()) continue
      try {
        if (readFileSync(`${processPath}/cgroup`, 'utf8') !== runnerCgroup) continue
      } catch (error) {
        if (disappeared(error)) continue
        return 'failed'
      }

      let handles: string[]
      try {
        handles = [`${processPath}/cwd`, ...readdirSync(`${processPath}/fd`).map((fd) => `${processPath}/fd/${fd}`)]
      } catch (error) {
        if (disappeared(error)) continue
        return 'failed'
      }
      for (const handle of handles) {
        try {
          if (within(root, realpathSync(handle))) return 'busy'
        } catch (error) {
          if (!disappeared(error)) return 'failed'
        }
      }
    }
  } catch {
    return 'failed'
  }
  return 'ready'
}

export function snapshotRunnerProcessIdentities(): Set<string> | null {
  if (process.platform !== 'linux') return null
  try {
    // The service cgroup retains detached descendants after their parent exits.
    const cgroup = readFileSync('/proc/self/cgroup', 'utf8')
      .trim()
      .split('\n')
      .find((line) => line.startsWith('0::'))
    if (!cgroup) return null
    const path = cgroup.slice(3)
    if (!path.startsWith('/') || path.includes('..')) return null
    const identities = new Set<string>()
    for (const pid of readFileSync(`/sys/fs/cgroup${path}/cgroup.procs`, 'utf8').trim().split(/\s+/)) {
      if (!/^\d+$/.test(pid)) continue
      let stat: string
      try {
        stat = readFileSync(`/proc/${pid}/stat`, 'utf8')
      } catch (error) {
        if (disappeared(error)) continue
        return null
      }
      const closing = stat.lastIndexOf(')')
      const fields =
        closing < 0
          ? []
          : stat
              .slice(closing + 2)
              .trim()
              .split(/\s+/)
      if (!fields[19]) return null
      if (fields[0] === 'Z' || fields[0] === 'X') continue
      identities.add(`${pid}:${fields[19]}`)
    }
    return identities
  } catch {
    return null
  }
}
