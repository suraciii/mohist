import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

test('root scripts expose the static Go CLI build and installer', () => {
  const packageJson = JSON.parse(readFileSync(resolve(repositoryRoot, 'package.json'), 'utf8')) as {
    scripts: Record<string, string>
  }

  assert.equal(
    packageJson.scripts['build:cli'],
    'CGO_ENABLED=0 go -C packages/go/mohist-cli build -tags netgo,osusergo -trimpath -buildvcs=false -o ../../../bin/build/mo ./cmd/mo',
  )
  assert.equal(
    packageJson.scripts['check:cli-go-format'],
    'test -z "$(gofmt -l packages/go/mohist-cli/*.go packages/go/mohist-cli/cmd/mo/*.go)"',
  )
  assert.equal(
    packageJson.scripts['install:cli'],
    'bash scripts/install-mo.sh',
  )
})

test('repository verification includes the Go CLI checks', () => {
  const config = readFileSync(resolve(repositoryRoot, 'test-duration.config.jsonc'), 'utf8')
  for (const check of [
    'npm", "args": ["run", "build:cli',
    'npm", "args": ["run", "check:cli-go-format',
    'npm", "args": ["run", "test:cli',
  ]) {
    assert.match(config, new RegExp(check.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')))
  }
})
