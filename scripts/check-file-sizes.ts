import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { dirname, extname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const maxLines = 1000
const governedExtensions = new Set(['.cs', '.ts', '.tsx'])
const governedRoot = 'packages/'
// EF regenerates the snapshot and *.Designer.cs wholesale on every model
// change, so their line counts track the schema, not authoring discipline.
const generatedExcludedPrefix = 'packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/'

export function countLines(content: string): number {
  if (content.length === 0) return 0
  return content.split(/\r\n|\r|\n/).length
}

export function allowedLineCount(baseLines: number | null): number {
  return baseLines == null || baseLines <= maxLines ? maxLines : baseLines
}

export function evaluateFileSize({ baseLines, candidateLines }: { baseLines: number | null; candidateLines: number }): {
  limit: number
  violates: boolean
} {
  const limit = allowedLineCount(baseLines)
  return { limit, violates: candidateLines > limit }
}

export function isGovernedPath(filePath: string): boolean {
  if (!filePath.startsWith(governedRoot)) return false
  if (filePath.startsWith(generatedExcludedPrefix)) return false
  return governedExtensions.has(extname(filePath))
}

export function parseChangedFiles(output: string): Array<{ status: string; path: string; oldPath?: string }> {
  const fields = output.split('\0')
  const changes = []

  for (let index = 0; index < fields.length - 1; ) {
    const status = fields[index]
    index += 1
    if (status.startsWith('R')) {
      changes.push({ status: status[0], oldPath: fields[index], path: fields[index + 1] })
      index += 2
    } else {
      changes.push({ status: status[0], path: fields[index] })
      index += 1
    }
  }

  return changes
}

export function gitText(args: string[], env: NodeJS.ProcessEnv = process.env): string {
  return execFileSync('git', args, {
    cwd: repositoryRoot,
    encoding: 'utf8',
    maxBuffer: 32 * 1024 * 1024,
    stdio: ['ignore', 'pipe', 'pipe'],
    env,
  })
}

export function resolveBaseRef(env: NodeJS.ProcessEnv = process.env): string {
  if (env.FILE_SIZE_BASE_REF) return env.FILE_SIZE_BASE_REF

  try {
    const mergeBase = gitText(['merge-base', 'origin/master', 'HEAD'], env).trim()
    const head = gitText(['rev-parse', 'HEAD'], env).trim()
    return mergeBase === head ? head : mergeBase
  } catch (error) {
    throw new Error(
      `Could not resolve the file-size base from origin/master: ${error instanceof Error ? error.message : String(error)}. ` +
        'Fetch origin/master or set FILE_SIZE_BASE_REF to an explicit commit.',
    )
  }
}

export function changedFilesUnder(
  baseRef: string,
  root: string,
): Array<{ status: string; path: string; oldPath?: string }> {
  const diff = gitText(['diff', '--name-status', '-z', '--find-renames=90%', baseRef, '--', root])
  const untracked = gitText(['ls-files', '--others', '--exclude-standard', '-z', '--', root])
    .split('\0')
    .filter((filePath) => filePath.length > 0)

  return [...parseChangedFiles(diff), ...untracked.map((path) => ({ status: 'A', path }))]
}

function readBaseFile(baseRef: string, filePath: string): string {
  try {
    return gitText(['show', `${baseRef}:${filePath}`])
  } catch {
    throw new Error(`Could not read ${filePath} from base ${baseRef}; fetch the base or set FILE_SIZE_BASE_REF`)
  }
}

function changedGovernedFiles(baseRef: string): Array<{ path: string; baseLines: number | null }> {
  return changedFilesUnder(baseRef, governedRoot)
    .filter((change) => change.status !== 'D')
    .filter((change) => isGovernedPath(change.path))
    .map((change) => ({
      path: change.path,
      baseLines: change.status === 'A' ? null : countLines(readBaseFile(baseRef, change.oldPath ?? change.path)),
    }))
}

function run(): number {
  const baseRef = resolveBaseRef()
  // Fail loudly instead of silently turning a missing or shallow base into a pass.
  gitText(['cat-file', '-e', `${baseRef}^{commit}`])

  const violations = []
  for (const change of changedGovernedFiles(baseRef)) {
    const candidateLines = countLines(readFileSync(resolve(repositoryRoot, change.path), 'utf8'))
    const result = evaluateFileSize({ baseLines: change.baseLines, candidateLines })
    if (!result.violates) continue

    const before = change.baseLines == null ? 'new file' : `${change.baseLines} lines`
    violations.push(
      `${change.path}: ${before} -> ${candidateLines} lines (allowed ${result.limit}). ` +
        'Split the file; a file already over the limit may not grow.',
    )
  }

  if (violations.length === 0) {
    console.log(`file sizes: ratchet clean against ${baseRef}`)
    return 0
  }

  console.error(`file size ratchet failed (base ${baseRef}):`)
  for (const violation of violations) console.error(`- ${violation}`)
  return 1
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = run()
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error))
    process.exitCode = 1
  }
}
