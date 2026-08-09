import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { test } from 'node:test'
import { checkDocumentation } from './check-docs.js'

function write(root: string, relativePath: string, content: string): void {
  const filePath = resolve(root, relativePath)
  mkdirSync(dirname(filePath), { recursive: true })
  writeFileSync(filePath, content)
}

function fixture(): string {
  const root = mkdtempSync(resolve(tmpdir(), 'mohist-docs-check-'))
  write(root, 'README.md', '# Mohist\n')
  write(root, 'CONTEXT.md', '# Context\n')
  write(root, 'CONTRIBUTING.md', '# Contributing\n')
  write(root, 'docs/README.md', '# Product documentation\n')
  write(root, 'design/README.md', '# Design documentation\n')
  return root
}

function withFixture(run: (root: string) => void): void {
  const root = fixture()
  try {
    run(root)
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
}

function rules(root: string): string[] {
  return checkDocumentation(root).violations.map((item) => item.rule)
}

test('accepts English Markdown, ASCII text diagrams, and structurally ignored link text', () => {
  withFixture((root) => {
    write(root, 'docs/guide.md', [
      '# Guide',
      '',
      '```text',
      '+--------+     +--------+',
      '| Server | --> | Runner |',
      '+--------+     +--------+',
      '```',
      '',
      '`[not a link](missing.md)`',
      '',
      '```text',
      '[also not a link](missing.md)',
      '```',
    ].join('\n'))
    write(root, 'history.md', '# Ignored\n\n中文\n\n```mermaid\na --> b\n```\n')

    assert.deepEqual(checkDocumentation(root).violations, [])
  })
})

test('rejects Han-script characters in every nested active document', () => {
  withFixture((root) => {
    write(root, 'docs/nested/guide.md', '# Guide\n\n中文\n')

    const result = checkDocumentation(root)
    assert.equal(result.violations.filter((item) => item.rule === 'english-only').length, 1)
    assert.ok(result.files.some((file) => file.endsWith('/docs/nested/guide.md')))
  })
})

test('rejects PlantUML and Mermaid fence languages case-insensitively', () => {
  withFixture((root) => {
    write(root, 'docs/diagrams.md', [
      '# Diagrams',
      '',
      '```Mermaid',
      'a --> b',
      '```',
      '',
      '```plantuml',
      '@startuml',
      '@enduml',
      '```',
    ].join('\n'))

    assert.equal(rules(root).filter((rule) => rule === 'ascii-diagram-only').length, 2)
  })
})

test('rejects Unicode box-drawing and arrow glyphs', () => {
  withFixture((root) => {
    write(root, 'design/diagram.md', '# Diagram\n\n┌┐└┘├┤┬┴┼─│►◄▲▼→←⇒\n')

    assert.equal(rules(root).filter((rule) => rule === 'ascii-diagram-only').length, 1)
  })
})

test('resolves duplicate headings, encoded fragments, references, queries, and directory READMEs', () => {
  withFixture((root) => {
    write(root, 'docs/topic.md', '# Cafe\n\n## Repeat\n\n## Repeat\n')
    write(root, 'docs/nested/README.md', '# Directory overview\n')
    write(root, 'docs/asset.txt', 'not Markdown')
    write(root, 'docs/README.md', [
      '# Product documentation',
      '',
      '[Encoded](topic.md?view=rendered#caf%65)',
      '[Duplicate](topic.md#repeat-1)',
      '[Directory](nested/?source=docs#directory-overview)',
      '[Reference][topic]',
      '[Asset](asset.txt?download=1#ignored-fragment)',
      '[Local](?view=rendered#product-documentation)',
      '[External](https://example.invalid/missing.md#missing)',
      '[Email](mailto:docs@example.invalid)',
      '[Absolute](/missing.md)',
      '',
      '[topic]: topic.md#caf%65',
    ].join('\n'))

    assert.deepEqual(checkDocumentation(root).violations, [])
  })
})

test('rejects missing relative file and asset targets', () => {
  withFixture((root) => {
    write(root, 'docs/README.md', [
      '# Product documentation',
      '',
      '[Missing document](missing.md)',
      '![Missing asset](missing.png)',
    ].join('\n'))

    assert.equal(rules(root).filter((rule) => rule === 'relative-link-target-exists').length, 2)
  })
})

test('rejects undefined link and image references', () => {
  withFixture((root) => {
    write(root, 'docs/README.md', [
      '# Product documentation',
      '',
      '[Missing reference][missing-link]',
      '![Missing image reference][missing-image]',
    ].join('\n'))

    assert.equal(rules(root).filter((rule) => rule === 'markdown-link-reference-exists').length, 2)
  })
})

test('rejects out-of-range duplicate and malformed heading fragments', () => {
  withFixture((root) => {
    write(root, 'docs/topic.md', '# Repeat\n\n# Repeat\n')
    write(root, 'docs/README.md', [
      '# Product documentation',
      '',
      '[Missing duplicate](topic.md?view=rendered#repeat-2)',
      '[Malformed](topic.md#bad%ZZfragment)',
    ].join('\n'))

    const resultRules = rules(root)
    assert.ok(resultRules.includes('markdown-heading-fragment-exists'))
    assert.ok(resultRules.includes('valid-relative-link'))
  })
})

test('requires a directory README when a directory link has a fragment', () => {
  withFixture((root) => {
    mkdirSync(resolve(root, 'docs/empty'))
    write(root, 'docs/README.md', '# Product documentation\n\n[Empty](empty/#heading)\n')

    assert.ok(rules(root).includes('markdown-heading-fragment-exists'))
  })
})
