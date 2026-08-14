import { execFileSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { dirname, extname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { changedFilesUnder, resolveBaseRef } from './check-file-sizes.js'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const formatExtensions = new Set(['.js', '.mjs', '.ts', '.tsx', '.css'])
const formatRoots = ['packages/', 'scripts/']
const biomeBin = join(repositoryRoot, 'node_modules', '@biomejs', 'biome', 'bin', 'biome')

export function isFormattablePath(filePath: string): boolean {
  return formatExtensions.has(extname(filePath))
}

function run(): number {
  const write = process.argv.includes('--write')
  const baseRef = resolveBaseRef()

  if (!existsSync(biomeBin)) {
    console.error(`biome is not installed; run npm ci at the repository root first`)
    return 1
  }

  const files = formatRoots
    .flatMap((root) => changedFilesUnder(baseRef, root))
    .filter((change) => change.status !== 'D')
    .filter((change) => isFormattablePath(change.path))
    .map((change) => change.path)

  if (files.length === 0) {
    console.log(`format: no formattable files changed against ${baseRef}`)
    return 0
  }

  try {
    execFileSync(process.execPath, [biomeBin, 'format', ...(write ? ['--write'] : []), ...files], {
      cwd: repositoryRoot,
      stdio: ['ignore', 'inherit', 'inherit'],
    })
  } catch {
    console.error(`format: ${files.length} changed file(s) are not biome-clean; run npm run format`)
    return 1
  }

  console.log(`format: ${files.length} changed file(s) are biome-clean against ${baseRef}`)
  return 0
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    process.exitCode = run()
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error))
    process.exitCode = 1
  }
}
const x = 1
