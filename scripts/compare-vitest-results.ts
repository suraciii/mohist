import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

function usage() {
  return [
    'Usage:',
    '  tsx scripts/compare-vitest-results.ts --before <report.json> --after <report.json> [--manifest <changes.json>]',
  ].join('\n')
}

function isRecord(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function parseArguments(arguments_) {
  const options = { before: undefined, after: undefined, manifest: undefined, help: false }

  for (let index = 0; index < arguments_.length; index += 1) {
    const argument = arguments_[index]
    if (argument === '--help' || argument === '-h') {
      options.help = true
      continue
    }
    if (argument === '--before' || argument === '--after' || argument === '--manifest') {
      const value = arguments_[index + 1]
      if (value === undefined || value.startsWith('--')) throw new Error(`Option ${argument} needs a file path.`)
      const optionName = argument.slice(2)
      if (options[optionName] !== undefined) throw new Error(`Option ${argument} may only be provided once.`)
      options[optionName] = value
      index += 1
      continue
    }
    throw new Error(`Unknown option: ${argument}`)
  }

  if (options.help) return options
  if (options.before === undefined || options.after === undefined) {
    throw new Error('--before and --after are required.\n\n' + usage())
  }
  return options
}

function readJson(filePath, label) {
  try {
    return JSON.parse(readFileSync(filePath, 'utf8'))
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    throw new Error(`Could not read ${label} JSON report ${filePath}: ${reason}`)
  }
}

function assertKnownKeys(value, allowedKeys, label) {
  for (const key of Object.keys(value)) {
    if (!allowedKeys.has(key)) throw new Error(`${label} has an unsupported property: ${key}`)
  }
}

function readManifest(filePath) {
  if (filePath === undefined) return { renames: [], removals: [] }

  const manifest = readJson(filePath, 'manifest')
  if (!isRecord(manifest)) throw new Error('Manifest must be a JSON object.')
  assertKnownKeys(manifest, new Set(['renames', 'removals']), 'Manifest')

  const renames = manifest.renames ?? []
  const removals = manifest.removals ?? []
  if (!Array.isArray(renames)) throw new Error('Manifest property renames must be an array.')
  if (!Array.isArray(removals)) throw new Error('Manifest property removals must be an array.')

  return {
    renames: renames.map((rename, index) => {
      const label = `Manifest renames[${index}]`
      if (!isRecord(rename)) throw new Error(`${label} must be an object.`)
      assertKnownKeys(rename, new Set(['from', 'to']), label)
      if (typeof rename.from !== 'string' || rename.from.length === 0) throw new Error(`${label}.from must be a non-empty string.`)
      if (typeof rename.to !== 'string' || rename.to.length === 0) throw new Error(`${label}.to must be a non-empty string.`)
      if (rename.from === rename.to) throw new Error(`${label} must change the fullName.`)
      return rename
    }),
    removals: removals.map((removal, index) => {
      const label = `Manifest removals[${index}]`
      if (!isRecord(removal)) throw new Error(`${label} must be an object.`)
      assertKnownKeys(removal, new Set(['fullName', 'reason']), label)
      if (typeof removal.fullName !== 'string' || removal.fullName.length === 0) {
        throw new Error(`${label}.fullName must be a non-empty string.`)
      }
      if (typeof removal.reason !== 'string' || removal.reason.trim().length === 0) {
        throw new Error(`${label}.reason must be a non-empty string.`)
      }
      return removal
    }),
  }
}

function collectAssertions(report, label) {
  if (!isRecord(report) || !Array.isArray(report.testResults)) {
    throw new Error(`${label} report must contain a testResults array.`)
  }
  if (report.success !== undefined && typeof report.success !== 'boolean') {
    throw new Error(`${label} report success must be a boolean when present.`)
  }

  const testResults = []
  const assertions = []

  for (const [resultIndex, testResult] of report.testResults.entries()) {
    const resultLabel = `${label} report testResults[${resultIndex}]`
    if (!isRecord(testResult)) throw new Error(`${resultLabel} must be an object.`)
    if (typeof testResult.status !== 'string') throw new Error(`${resultLabel}.status must be a string.`)
    if (!Array.isArray(testResult.assertionResults)) throw new Error(`${resultLabel}.assertionResults must be an array.`)

    testResults.push({
      name: typeof testResult.name === 'string' ? testResult.name : `testResults[${resultIndex}]`,
      status: testResult.status,
    })

    for (const [assertionIndex, assertion] of testResult.assertionResults.entries()) {
      const assertionLabel = `${resultLabel}.assertionResults[${assertionIndex}]`
      if (!isRecord(assertion)) throw new Error(`${assertionLabel} must be an object.`)
      if (typeof assertion.fullName !== 'string' || assertion.fullName.length === 0) {
        throw new Error(`${assertionLabel}.fullName must be a non-empty string.`)
      }
      if (typeof assertion.status !== 'string') throw new Error(`${assertionLabel}.status must be a string.`)
      assertions.push({ fullName: assertion.fullName, status: assertion.status, resultName: testResults.at(-1).name })
    }
  }

  return { assertions, testResults, success: report.success }
}

function nonPassedAfterResults(after) {
  const failures = []
  if (after.success === false) failures.push('report success is false')
  for (const testResult of after.testResults) {
    if (testResult.status !== 'passed') failures.push(`test file ${JSON.stringify(testResult.name)} is ${testResult.status}`)
  }
  for (const assertion of after.assertions) {
    if (assertion.status !== 'passed') {
      failures.push(`test ${JSON.stringify(assertion.fullName)} in ${JSON.stringify(assertion.resultName)} is ${assertion.status}`)
    }
  }
  return failures
}

function countByFullName(assertions) {
  const counts = new Map()
  for (const assertion of assertions) counts.set(assertion.fullName, (counts.get(assertion.fullName) ?? 0) + 1)
  return counts
}

function removeCount(counts, fullName) {
  const count = counts.get(fullName)
  if (count === undefined) return false
  if (count === 1) counts.delete(fullName)
  else counts.set(fullName, count - 1)
  return true
}

function countEntries(counts) {
  let total = 0
  for (const count of counts.values()) total += count
  return total
}

function formatCounts(counts) {
  return [...counts.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([fullName, count]) => `  - ${JSON.stringify(fullName)}${count === 1 ? '' : ` (${count} occurrences)`}`)
    .join('\n')
}

function unmatchedCounts(beforeCounts, afterCounts) {
  const missing = new Map(beforeCounts)
  const additions = new Map(afterCounts)

  for (const [fullName, beforeCount] of beforeCounts) {
    const afterCount = afterCounts.get(fullName) ?? 0
    const unchangedCount = Math.min(beforeCount, afterCount)
    for (let index = 0; index < unchangedCount; index += 1) {
      removeCount(missing, fullName)
      removeCount(additions, fullName)
    }
  }

  return { missing, additions }
}

function compareVitestResults(beforeReport, afterReport, manifest = { renames: [], removals: [] }) {
  const before = collectAssertions(beforeReport, 'Before')
  const after = collectAssertions(afterReport, 'After')
  const nonPassed = nonPassedAfterResults(after)
  if (nonPassed.length > 0) {
    throw new Error(`After report contains non-passed results:\n${nonPassed.map((failure) => `  - ${failure}`).join('\n')}`)
  }

  const { missing, additions } = unmatchedCounts(countByFullName(before.assertions), countByFullName(after.assertions))
  const initialMissingCount = countEntries(missing)

  for (const removal of manifest.removals) {
    if (!removeCount(missing, removal.fullName)) {
      throw new Error(`Manifest removal does not match a missing baseline identity: ${JSON.stringify(removal.fullName)}`)
    }
  }

  for (const rename of manifest.renames) {
    if (!removeCount(missing, rename.from)) {
      throw new Error(`Manifest rename source does not match a missing baseline identity: ${JSON.stringify(rename.from)}`)
    }
    if (!removeCount(additions, rename.to)) {
      throw new Error(`Manifest rename target does not match a new after identity: ${JSON.stringify(rename.to)}`)
    }
  }

  if (missing.size > 0) {
    throw new Error(`Missing baseline test identities:\n${formatCounts(missing)}`)
  }

  return {
    beforeAssertions: before.assertions.length,
    afterAssertions: after.assertions.length,
    unchangedAssertions: before.assertions.length - initialMissingCount,
    additions: countEntries(additions),
    renames: manifest.renames.length,
    removals: manifest.removals.length,
  }
}

function main() {
  const options = parseArguments(process.argv.slice(2))
  if (options.help) {
    console.log(usage())
    return
  }
  const summary = compareVitestResults(
    readJson(options.before, 'before'),
    readJson(options.after, 'after'),
    readManifest(options.manifest),
  )
  console.log(
    `Vitest results match: ${summary.unchangedAssertions} retained, ${summary.additions} additions, ${summary.renames} renames, ${summary.removals} removals.`,
  )
}

const isMainModule = process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)

if (isMainModule) {
  try {
    main()
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    console.error(message)
    process.exitCode = 1
  }
}

export { compareVitestResults }
