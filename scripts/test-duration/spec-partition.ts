import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

export interface PartitionPlan {
  readonly index: number
  readonly count: number
  readonly allClasses: readonly string[]
  readonly selectedClasses: readonly string[]
  readonly selectedCaseCount: number
  readonly totalCaseCount: number
}

export interface PartitionArtifact {
  readonly directory: string
  readonly index: number
  readonly count: number
  readonly allClasses: readonly string[]
  readonly selectedClasses: readonly string[]
}

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0
}

function requireInteger(value: number, name: string): void {
  if (!Number.isInteger(value) || value < 0) throw new Error(`${name} must be a non-negative integer`)
}

function parseClassNames(discoveredOutput: string): string[] {
  const rawClasses = discoveredOutput
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
  if (rawClasses.length === 0) throw new Error('class discovery returned no classes')
  for (const className of rawClasses) {
    if (!className.startsWith('Mohist.Server.SpecTests.')) {
      throw new Error(`unexpected discovered class: ${className}`)
    }
    if (/\s/.test(className)) throw new Error(`discovered class contains whitespace: ${className}`)
  }
  const classes = [...rawClasses].sort(compareText)
  for (let index = 1; index < classes.length; index++) {
    if (classes[index] === classes[index - 1]) {
      throw new Error(`class discovery returned duplicate classes: ${classes[index]}`)
    }
  }
  return classes
}

function parseCaseCounts(testDiscoveryOutput: string, classes: readonly string[]): Map<string, number> {
  const counts = new Map(classes.map((className) => [className, 0]))
  const prefixes = [...classes].sort((left, right) => right.length - left.length || compareText(left, right))
  for (const line of testDiscoveryOutput
    .split(/\r?\n/)
    .map((value) => value.trim())
    .filter(Boolean)) {
    const owner = prefixes.find((className) => line.startsWith(`${className}.`))
    if (owner === undefined) throw new Error(`test discovery returned unknown test: ${line}`)
    counts.set(owner, counts.get(owner)! + 1)
  }
  for (const className of classes) {
    if (counts.get(className) === 0) throw new Error(`test discovery returned no tests for class: ${className}`)
  }
  return counts
}

function assignWeightedClasses(
  classes: readonly string[],
  caseCounts: ReadonlyMap<string, number>,
  partitionCount: number,
) {
  const buckets = Array.from({ length: partitionCount }, (_, index) => ({ index, total: 0, classes: [] as string[] }))
  const weighted = classes
    .map((className) => ({ className, cases: caseCounts.get(className) ?? 0 }))
    .sort((left, right) => right.cases - left.cases || compareText(left.className, right.className))
  for (const item of weighted) {
    const bucket = buckets.reduce((best, current) => (current.total < best.total ? current : best))
    bucket.classes.push(item.className)
    bucket.total += item.cases
  }
  return buckets
}

export function planPartitionClasses(
  discoveredOutput: string,
  partitionIndex: number,
  partitionCount: number,
  testDiscoveryOutput?: string,
): PartitionPlan {
  requireInteger(partitionIndex, 'partition-index')
  requireInteger(partitionCount, 'partition-count')
  if (partitionCount === 0) throw new Error('partition-count must be greater than zero')
  if (partitionIndex >= partitionCount) throw new Error('partition-index must be less than partition-count')

  const classes = parseClassNames(discoveredOutput)
  const caseCounts =
    testDiscoveryOutput === undefined
      ? new Map(classes.map((className) => [className, 1]))
      : parseCaseCounts(testDiscoveryOutput, classes)
  const buckets = assignWeightedClasses(classes, caseCounts, partitionCount)
  const selectedClasses = [...buckets[partitionIndex].classes].sort(compareText)
  if (selectedClasses.length === 0) throw new Error(`partition ${partitionIndex} has no classes`)
  return {
    index: partitionIndex,
    count: partitionCount,
    allClasses: classes,
    selectedClasses,
    selectedCaseCount: buckets[partitionIndex].total,
    totalCaseCount: classes.reduce((total, className) => total + caseCounts.get(className)!, 0),
  }
}

export function verifyPartitionArtifacts(artifacts: readonly PartitionArtifact[]): {
  readonly classes: number
  readonly partitions: number
} {
  if (artifacts.length === 0) throw new Error('no partition artifacts were downloaded')
  const first = artifacts[0]
  if (first.count !== artifacts.length) {
    throw new Error(`expected ${first.count} partition artifacts, found ${artifacts.length}`)
  }
  const indexes = new Set<number>()
  const selected = new Set<string>()
  const canonicalClasses = [...first.allClasses].sort(compareText)
  if (canonicalClasses.length === 0) throw new Error('partition artifact has no discovered classes')
  for (const artifact of artifacts) {
    if (artifact.count !== first.count) throw new Error('partitions declare different counts')
    if (artifact.index < 0 || artifact.index >= artifact.count) {
      throw new Error(`partition index ${artifact.index} is outside count ${artifact.count}`)
    }
    if (indexes.has(artifact.index)) throw new Error(`duplicate partition index: ${artifact.index}`)
    indexes.add(artifact.index)
    const all = [...artifact.allClasses].sort(compareText)
    if (all.length !== canonicalClasses.length || all.some((value, index) => value !== canonicalClasses[index])) {
      throw new Error('partitions discovered different class lists')
    }
    for (const className of artifact.selectedClasses) {
      if (selected.has(className)) throw new Error(`classes selected more than once: ${className}`)
      selected.add(className)
    }
  }
  for (let index = 0; index < first.count; index++) {
    if (!indexes.has(index)) throw new Error(`missing partition index: ${index}`)
  }
  if (selected.size !== canonicalClasses.length || canonicalClasses.some((className) => !selected.has(className))) {
    throw new Error('selected class union does not equal complete discovered class list')
  }
  return { classes: canonicalClasses.length, partitions: first.count }
}

function writePlan(manifestDirectory: string, plan: PartitionPlan): void {
  mkdirSync(manifestDirectory, { recursive: true })
  writeFileSync(resolve(manifestDirectory, 'all-classes.txt'), `${plan.allClasses.join('\n')}\n`)
  writeFileSync(resolve(manifestDirectory, 'selected-classes.txt'), `${plan.selectedClasses.join('\n')}\n`)
  writeFileSync(
    resolve(manifestDirectory, 'partition.txt'),
    `index=${plan.index}\ncount=${plan.count}\ntotal_classes=${plan.allClasses.length}\nselected_classes=${plan.selectedClasses.length}\ntotal_cases=${plan.totalCaseCount}\nselected_cases=${plan.selectedCaseCount}\n`,
  )
}

function readLines(path: string): string[] {
  return readFileSync(path, 'utf8').split(/\r?\n/).filter(Boolean)
}

function parseMetadata(path: string): { readonly index: number; readonly count: number } {
  const values = new Map<string, string>()
  for (const line of readLines(path)) {
    const separator = line.indexOf('=')
    if (separator > 0) values.set(line.slice(0, separator), line.slice(separator + 1))
  }
  const index = Number(values.get('index'))
  const count = Number(values.get('count'))
  requireInteger(index, 'index')
  requireInteger(count, 'count')
  if (count === 0) throw new Error('partition metadata has a zero count')
  return { index, count }
}

function loadArtifacts(directory: string): PartitionArtifact[] {
  if (!existsSync(directory) || !statSync(directory).isDirectory()) {
    throw new Error(`artifact directory does not exist: ${directory}`)
  }
  return readdirSync(directory, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
    .sort(compareText)
    .map((name) => {
      const partitionDirectory = resolve(directory, name)
      const allPath = resolve(partitionDirectory, 'all-classes.txt')
      const selectedPath = resolve(partitionDirectory, 'selected-classes.txt')
      const metadataPath = resolve(partitionDirectory, 'partition.txt')
      if (!existsSync(allPath) || !existsSync(selectedPath) || !existsSync(metadataPath)) {
        throw new Error(`partition artifact is incomplete: ${partitionDirectory}`)
      }
      const metadata = parseMetadata(metadataPath)
      return {
        directory: partitionDirectory,
        index: metadata.index,
        count: metadata.count,
        allClasses: readLines(allPath),
        selectedClasses: readLines(selectedPath),
      }
    })
}

async function runCommand(
  command: string,
  args: readonly string[],
): Promise<{ readonly exitCode: number | null; readonly output: string }> {
  const child = spawn(command, args as string[], {
    cwd: repoRoot,
    stdio: ['ignore', 'pipe', 'pipe'],
  })
  let output = ''
  const capture = (chunk: Buffer, stream: NodeJS.WriteStream) => {
    const text = chunk.toString()
    output += text
    stream.write(chunk)
  }
  child.stdout?.on('data', (chunk: Buffer) => capture(chunk, process.stdout))
  child.stderr?.on('data', (chunk: Buffer) => capture(chunk, process.stderr))
  return new Promise((resolveResult) => {
    child.once('error', () => resolveResult({ exitCode: 1, output }))
    child.once('close', (exitCode) => resolveResult({ exitCode, output }))
  })
}

async function runPartition(
  apphost: string,
  partitionIndex: number,
  partitionCount: number,
  manifestDirectory: string,
  reportPath: string,
  maxThreads = 1,
): Promise<void> {
  if (!existsSync(apphost)) throw new Error(`apphost does not exist: ${apphost}`)
  if (!Number.isInteger(maxThreads) || maxThreads <= 0) throw new Error('max-threads must be a positive integer')
  const discoveryArgs = ['-noColor', '-noLogo', '-noAutoReporters']
  const discovery = await runCommand(apphost, ['-list', 'classes', ...discoveryArgs])
  if (discovery.exitCode !== 0) throw new Error('xUnit class discovery failed')
  const testDiscovery = await runCommand(apphost, ['-list', 'tests', '-preEnumerateTheories', ...discoveryArgs])
  if (testDiscovery.exitCode !== 0) throw new Error('xUnit test discovery failed')
  const plan = planPartitionClasses(discovery.output, partitionIndex, partitionCount, testDiscovery.output)
  writePlan(manifestDirectory, plan)
  console.log(
    `Spec partition ${plan.index + 1}/${plan.count}: ${plan.selectedClasses.length} of ${plan.allClasses.length} classes (${plan.selectedCaseCount}/${plan.totalCaseCount} cases)`,
  )
  mkdirSync(dirname(reportPath), { recursive: true })
  const classArgs = plan.selectedClasses.flatMap((className) => ['-class', className])
  const execution = await runCommand(apphost, [
    '-noColor',
    '-noLogo',
    '-noAutoReporters',
    '-parallel',
    'collections',
    '-parallelAlgorithm',
    'conservative',
    '-maxThreads',
    String(maxThreads),
    '-trx',
    reportPath,
    ...classArgs,
  ])
  writeFileSync(resolve(manifestDirectory, 'spec.log'), execution.output)
  if (execution.exitCode !== 0)
    throw new Error(`xUnit partition exited ${execution.exitCode ?? 'without an exit code'}`)
  if (!existsSync(reportPath) || statSync(reportPath).size === 0) {
    throw new Error(`xUnit did not write a TRX report: ${reportPath}`)
  }
  if (!/Total:\s*[1-9][0-9]*/.test(execution.output)) {
    throw new Error('xUnit completed without executing any tests')
  }
}

function usage(): never {
  throw new Error(
    'usage: spec-partition <run apphost index count manifest-dir report [max-threads] | verify manifest-root>',
  )
}

async function main(argv: readonly string[] = process.argv.slice(2)): Promise<number> {
  try {
    if (argv[0] === 'run' && (argv.length === 6 || argv.length === 7)) {
      await runPartition(
        argv[1],
        Number(argv[2]),
        Number(argv[3]),
        argv[4],
        argv[5],
        argv[6] === undefined ? 1 : Number(argv[6]),
      )
      return 0
    }
    if (argv[0] === 'verify' && argv.length === 2) {
      const result = verifyPartitionArtifacts(loadArtifacts(argv[1]))
      console.log(`Spec partition coverage verified: ${result.classes} classes across ${result.partitions} partitions`)
      return 0
    }
    usage()
  } catch (error) {
    process.stderr.write(`spec-partition: ${(error as Error).message}\n`)
    return 2
  }
}

const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href
if (isMain) {
  void main().then(
    (code) => process.exit(code),
    (error) => {
      process.stderr.write(`spec-partition: fatal error: ${(error as Error).message}\n`)
      process.exit(1)
    },
  )
}
