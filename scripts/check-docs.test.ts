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
      '```text diagram',
      '+--------+     +--------+',
      '| Server | --> | Runner |',
      '+--------+     +--------+',
      '```',
      '',
      '`[not a link](missing.md)`',
      '',
      '```text literal',
      '[also not a link](missing.md)',
      '```',
    ].join('\n'))
    write(root, 'history.md', '# Ignored\n\n中文\n\n```mermaid\na --> b\n```\n')

    assert.deepEqual(checkDocumentation(root).violations, [])
  })
})

test('rejects non-Latin writing scripts in every nested active document', () => {
  withFixture((root) => {
    write(root, 'docs/nested/guide.md', '# Guide\n\n中文\n\nРусский\n\nかな\n\n한국어\n')

    const result = checkDocumentation(root)
    assert.equal(result.violations.filter((item) => item.rule === 'latin-script-prose-only').length, 4)
    assert.ok(result.files.some((file) => file.endsWith('/docs/nested/guide.md')))
  })
})

test('checks prose scripts but allows Latin accents and exact inline and fenced code literals', () => {
  withFixture((root) => {
    write(root, 'docs/literals.md', [
      '# Literals',
      '',
      'This prose contains 中文.',
      '',
      'Latin-script prose may name Café.',
      '',
      'The exact provider value is `状态`.',
      '',
      '```text literal',
      '状态',
      '```',
      '',
      '```json',
      '{"status":"状态"}',
      '```',
    ].join('\n'))

    assert.equal(rules(root).filter((rule) => rule === 'latin-script-prose-only').length, 1)
  })
})

test('checks image alt and link titles as prose and rejects raw HTML', () => {
  withFixture((root) => {
    write(root, 'docs/topic.md', '# Topic\n')
    write(root, 'docs/asset.png', 'asset')
    write(root, 'docs/prose-fields.md', [
      '# Prose fields',
      '',
      '![中文](asset.png "标题")',
      '[Inline link](topic.md "链接")',
      '[Reference link][topic]',
      '',
      '[topic]: topic.md "引用"',
      '',
      '<div>',
      '中文',
      '</div>',
    ].join('\n'))

    const result = checkDocumentation(root)
    assert.equal(result.violations.filter((item) => item.rule === 'latin-script-prose-only').length, 4)
    assert.equal(result.violations.filter((item) => item.rule === 'raw-html-not-allowed').length, 1)
  })
})

test('rejects known non-text diagram fence languages case-insensitively', () => {
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
      '',
      '```DOT',
      'digraph { a -> b }',
      '```',
      '',
      '```{.GraphViz}',
      'digraph { a -> b }',
      '```',
      '',
      '```pikchr',
      'box "Client"; arrow; box "Server"',
      '```',
      '',
      '```ditaa',
      '+--------+   +--------+',
      '```',
      '',
      '```svgbob',
      'Client --> Server',
      '```',
    ].join('\n'))

    const violations = checkDocumentation(root).violations
      .filter((item) => item.rule === 'ascii-diagram-only')
    assert.equal(violations.length, 7)
    assert.ok(violations.every((item) => item.description.includes('`text diagram`')))
  })
})

test('rejects Unicode line art in fenced text diagrams', () => {
  withFixture((root) => {
    write(root, 'design/diagram.md', [
      '# Diagram',
      '',
      '```text diagram',
      '┌┐└┘├┤┬┴┼─│►◄▲▼→←⇒',
      '```',
    ].join('\n'))

    assert.equal(rules(root).filter((rule) => rule === 'ascii-diagram-only').length, 1)
  })
})

test('enforces ASCII only for explicit text diagrams and preserves literal and source code', () => {
  withFixture((root) => {
    write(root, 'design/diagram.md', [
      '# Diagram boundaries',
      '',
      '```text diagram',
      'Client -> Café',
      '```',
      '',
      '```text literal',
      'provider transition: old -> new',
      'exact value: 状态 → ready',
      '```',
      '',
      '```typescript',
      "const arrow = '→'",
      "const state = '状态'",
      '```',
    ].join('\n'))

    const result = checkDocumentation(root)
    assert.equal(result.violations.filter((item) => item.rule === 'ascii-diagram-only').length, 1)
    assert.equal(result.violations.filter((item) => item.rule === 'latin-script-prose-only').length, 0)
  })
})

test('does not infer diagrams from ordinary unlabeled source code', () => {
  withFixture((root) => {
    write(root, 'design/diagram.md', [
      '# Diagram',
      '',
      '```',
      'fn answer() -> i32 {',
      '  42',
      '}',
      '```',
    ].join('\n'))

    assert.deepEqual(checkDocumentation(root).violations, [])
  })
})

test('requires every text fence to declare exactly diagram or literal metadata', () => {
  withFixture((root) => {
    write(root, 'design/text-fences.md', [
      '# Text fences',
      '',
      '```text',
      'unclassified',
      '```',
      '',
      '```text output',
      'provider output',
      '```',
      '',
      '```text diagram extra',
      'Client -> Server',
      '```',
    ].join('\n'))

    assert.equal(rules(root).filter((rule) => rule === 'text-fence-kind-required').length, 3)
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

test('resolves duplicate reference definitions using CommonMark first-wins semantics', () => {
  withFixture((root) => {
    write(root, 'docs/topic.md', '# Topic\n')
    write(root, 'docs/README.md', [
      '# Product documentation',
      '',
      '[Broken first][broken]',
      '[Valid first][valid]',
      '',
      '[broken]: missing.md',
      '[broken]: topic.md',
      '[valid]: topic.md',
      '[valid]: missing.md',
    ].join('\n'))

    const violations = checkDocumentation(root).violations
      .filter((item) => item.rule === 'relative-link-target-exists')
    assert.equal(violations.length, 1)
    assert.match(violations[0].description, /missing\.md/u)
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
