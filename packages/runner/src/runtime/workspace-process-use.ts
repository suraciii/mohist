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

type ReadProcFile = (path: string, encoding: 'utf8') => string

export function snapshotRunnerProcessIdentities(readFile: ReadProcFile = readFileSync): Set<string> | null {
  if (process.platform !== 'linux') return null
  try {
    // The service cgroup retains detached descendants after their parent exits.
    const cgroup = readFile('/proc/self/cgroup', 'utf8')
      .trim()
      .split('\n')
      .find((line) => line.startsWith('0::'))
    if (!cgroup) return null
    const path = cgroup.slice(3)
    if (!path.startsWith('/') || path.includes('..')) return null

    const membersPath = `/sys/fs/cgroup${path}/cgroup.procs`
    const readMembers = (): Set<string> | null => {
      const members = new Set(readFile(membersPath, 'utf8').trim().split(/\s+/).filter(Boolean))
      return [...members].every((pid) => /^\d+$/.test(pid)) ? members : null
    }
    const sameMembers = (left: ReadonlySet<string>, right: ReadonlySet<string>) =>
      left.size === right.size && [...left].every((pid) => right.has(pid))
    const sameIdentities = (left: ReadonlyMap<string, string>, right: ReadonlyMap<string, string>) =>
      left.size === right.size && [...left].every(([pid, identity]) => right.get(pid) === identity)
    const scan = (): Map<string, string> | null => {
      const before = readMembers()
      if (!before) return null
      const identities = new Map<string, string>()
      for (const pid of before) {
        // A vanished PID may have forked after cgroup.procs was read.
        const stat = readFile(`/proc/${pid}/stat`, 'utf8')
        const closing = stat.lastIndexOf(')')
        const fields =
          closing < 0
            ? []
            : stat
                .slice(closing + 2)
                .trim()
                .split(/\s+/)
        if (!fields[19]) return null
        const state = fields[0] === 'Z' || fields[0] === 'X' ? 'terminated' : 'alive'
        identities.set(pid, `${fields[19]}:${state}`)
      }
      const after = readMembers()
      return after && sameMembers(before, after) ? identities : null
    }

    const first = scan()
    const second = scan()
    if (!first || !second || !sameIdentities(first, second)) return null
    const identities = new Set<string>()
    for (const [pid, identity] of second) {
      const [startTime, state] = identity.split(':')
      if (state === 'alive') identities.add(`${pid}:${startTime}`)
    }
    return identities
  } catch {
    return null
  }
}
