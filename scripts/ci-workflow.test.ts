import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { parseSuiteConfig } from './test-duration/config.js'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

function parseJobs(source: string): Map<string, string> {
  const jobsMarker = '\njobs:\n'
  const jobsStart = source.indexOf(jobsMarker)
  assert.notEqual(jobsStart, -1, 'workflow must declare a jobs block')
  const jobsSource = source.slice(jobsStart + jobsMarker.length)
  const matches = [...jobsSource.matchAll(/^  ([a-zA-Z0-9_-]+):\s*$/gm)]
  const jobs = new Map<string, string>()
  for (const [index, match] of matches.entries()) {
    const start = match.index! + match[0].length
    const end = matches[index + 1]?.index ?? jobsSource.length
    jobs.set(match[1]!, jobsSource.slice(start, end))
  }
  return jobs
}

function parseInlineNeeds(jobSource: string): string[] {
  const match = jobSource.match(/^    needs:\s*\[([^\]]*)\]\s*$/m)
  if (!match) return []
  return match[1]!
    .split(',')
    .map((value) => value.trim())
    .filter(Boolean)
}

test('CI workflow needs reference existing canonical producer jobs', () => {
  const workflow = readFileSync(resolve(repositoryRoot, '.github/workflows/ci.yml'), 'utf8')
  const jobs = parseJobs(workflow)

  for (const [jobID, jobSource] of jobs) {
    for (const dependency of parseInlineNeeds(jobSource)) {
      assert.ok(jobs.has(dependency), `${jobID} needs missing job ${dependency}`)
    }
  }

  const config = parseSuiteConfig(readFileSync(resolve(repositoryRoot, 'test-duration.config.jsonc'), 'utf8'))
  const expected = [...config.plan!.applications, config.plan!.repositoryScope].sort()
  assert.deepEqual(parseInlineNeeds(jobs.get('gate') ?? '').sort(), expected)
  assert.match(jobs.get('repository') ?? '', /npm run test:slack:race/)
})
