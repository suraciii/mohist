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

  assert.match(workflow, /^  NPM_CONFIG_AUDIT: "false"$/m)
  assert.match(workflow, /^  NPM_CONFIG_PREFER_OFFLINE: "true"$/m)

  for (const [jobID, jobSource] of jobs) {
    for (const dependency of parseInlineNeeds(jobSource)) {
      assert.ok(jobs.has(dependency), `${jobID} needs missing job ${dependency}`)
    }
  }

  const config = parseSuiteConfig(readFileSync(resolve(repositoryRoot, 'test-duration.config.jsonc'), 'utf8'))
  const expected = [...config.plan!.applications, config.plan!.repositoryScope, 'changes'].sort()
  assert.deepEqual(parseInlineNeeds(jobs.get('gate') ?? '').sort(), expected)
  assert.match(jobs.get('repository') ?? '', /npm run test:slack:race/)
})

test('CI evidence Gate consumes a standalone bundle without reinstalling the monorepo', () => {
  const workflow = readFileSync(resolve(repositoryRoot, '.github/workflows/ci.yml'), 'utf8')
  const jobs = parseJobs(workflow)
  const repository = jobs.get('repository') ?? ''
  const gate = jobs.get('gate') ?? ''

  assert.match(repository, /esbuild scripts\/test-duration\/gate\.ts .*--outfile=artifacts\/gate\.mjs/)
  assert.doesNotMatch(gate, /npm ci/)
  assert.match(gate, /node artifacts\/gate\.mjs --evidence-root/)
})

// Path filtering policy: the changes job owns the scope map, application
// jobs are conditioned on their lane, and shared build inputs force every
// scope. Each application lane lists the package roots it owns.
test('CI workflow path filters match the real dependency graph', () => {
  const workflow = readFileSync(resolve(repositoryRoot, '.github/workflows/ci.yml'), 'utf8')
  const jobs = parseJobs(workflow)
  const changes = jobs.get('changes') ?? ''
  assert.match(changes, /dorny\/paths-filter@v3/, 'changes job must use paths-filter')

  const config = parseSuiteConfig(readFileSync(resolve(repositoryRoot, 'test-duration.config.jsonc'), 'utf8'))
  for (const application of config.plan!.applications) {
    const jobSource = jobs.get(application) ?? ''
    assert.match(jobSource, /needs: \[changes\]/, `${application} must depend on the changes job`)
    assert.match(
      jobSource,
      new RegExp(`needs\\.changes\\.outputs\\.${application} == 'true'`),
      `${application} must be gated on its own change lane`,
    )
    assert.match(changes, new RegExp(`^            ${application}:$`, 'm'), `missing filter lane for ${application}`)
  }

  // Shared build inputs must wake every scope.
  const globalLane = changes.match(/^            global:\n((?:              - .*\n)+)/m)
  assert.notEqual(globalLane, null, 'global trigger lane is required')
  for (const pattern of ['.github/workflows/**', 'scripts/**', 'Directory.Build.props', 'package-lock.json']) {
    assert.ok(globalLane![1]!.includes(`'${pattern}'`), `global triggers must include ${pattern}`)
  }
  // Build-graph couplings discovered from ProjectReferences.
  const laneBlock = (lane: string): string => {
    const match = changes.match(new RegExp(`^            ${lane}:\\n((?:              - .*\\n)+)`, 'm'))
    return match?.[1] ?? ''
  }
  assert.ok(laneBlock('server').includes("'packages/server/**'"), 'server lane must include packages/server')

  const gate = jobs.get('gate') ?? ''
  assert.match(gate, /--skipped-scopes/, 'gate must receive the skipped-scope manifest')
})
