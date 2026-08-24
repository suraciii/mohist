import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { readdirSync, readFileSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const slackRoot = resolve(repositoryRoot, 'packages/go/mohist-slack')

function goFilesUnder(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) return goFilesUnder(path)
    return entry.isFile() && entry.name.endsWith('.go') ? [relative(repositoryRoot, path)] : []
  })
}

test('root build produces a non-runtime Slack artifact', () => {
  const packageJson = JSON.parse(readFileSync(resolve(repositoryRoot, 'package.json'), 'utf8')) as {
    scripts: Record<string, string>
  }

  assert.equal(
    packageJson.scripts['build:slack'],
    'go -C packages/go/mohist-slack build -tags netgo,osusergo -buildvcs=false -o bin/build/ ./cmd/mohist-slack',
  )
  assert.equal(packageJson.scripts['test:slack:race'], 'go -C packages/go/mohist-slack test -race ./...')
  assert.match(packageJson.scripts.build, /npm run build:slack/)
})

test('Slack Go sources are gofmt-clean', () => {
  const files = goFilesUnder(slackRoot)
  const output = execFileSync('gofmt', ['-l', ...files], {
    cwd: repositoryRoot,
    encoding: 'utf8',
  })

  assert.equal(output.trim(), '')
})
