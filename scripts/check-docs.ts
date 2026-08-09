import { existsSync, lstatSync, readdirSync, readFileSync } from 'node:fs'
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import GithubSlugger from 'github-slugger'
import type { Root } from 'mdast'
import { toString } from 'mdast-util-to-string'
import remarkLintNoUndefinedReferences from 'remark-lint-no-undefined-references'
import remarkParse from 'remark-parse'
import { unified } from 'unified'
import { VFile } from 'vfile'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const requiredDocuments = ['README.md', 'CONTEXT.md', 'CONTRIBUTING.md']
const documentationDirectories = ['docs', 'design']
// These fence names identify diagrams without interpreting ordinary source-code examples.
const forbiddenDiagramLanguages = new Set([
  'actdiag',
  'bob',
  'blockdiag',
  'c4',
  'c4plantuml',
  'd2',
  'diagram',
  'ditaa',
  'dot',
  'erd',
  'flowchart',
  'graph-easy',
  'graphviz',
  'gv',
  'kroki',
  'mermaid',
  'nomnoml',
  'nwdiag',
  'packetdiag',
  'pikchr',
  'plantuml',
  'puml',
  'rackdiag',
  'seqdiag',
  'structurizr',
  'svgbob',
  'tikz',
  'vega',
  'vega-lite',
  'wavedrom',
])
const nonLatinLetter = /(?=\p{Letter})[^\p{Script=Latin}]/gu
const nonAsciiCharacter = /[^\x00-\x7f]/u
const absoluteUrl = /^[a-z][a-z\d+.-]*:/iu
const markdownParser = unified()
  .use(remarkParse)
  .use(remarkLintNoUndefinedReferences, { allowShortcutLink: true })

type MarkdownPosition = {
  start: { line: number; column: number }
}

type MarkdownNode = {
  type: string
  alt?: string | null
  children?: MarkdownNode[]
  identifier?: string
  lang?: string | null
  meta?: string | null
  position?: MarkdownPosition
  title?: string | null
  url?: string
  value?: string
}

export type DocumentationViolation = {
  filePath: string
  line: number
  column: number
  rule: string
  description: string
}

export type DocumentationCheckResult = {
  files: string[]
  violations: DocumentationViolation[]
}

function walk(node: MarkdownNode, visit: (node: MarkdownNode) => void): void {
  visit(node)
  for (const child of node.children ?? []) walk(child, visit)
}

function positionOf(node: MarkdownNode): { line: number; column: number } {
  return node.position?.start ?? { line: 1, column: 1 }
}

function violation(
  filePath: string,
  node: MarkdownNode | undefined,
  rule: string,
  description: string,
): DocumentationViolation {
  const position = node === undefined ? { line: 1, column: 1 } : positionOf(node)
  return { filePath, ...position, rule, description }
}

function collectMarkdownFiles(directoryPath: string): string[] {
  const files: string[] = []
  for (const entry of readdirSync(directoryPath, { withFileTypes: true })) {
    const entryPath = resolve(directoryPath, entry.name)
    if (entry.isDirectory()) {
      files.push(...collectMarkdownFiles(entryPath))
    } else if (entry.isFile() && entry.name.endsWith('.md')) {
      files.push(entryPath)
    }
  }
  return files
}

function findDocumentationFiles(root: string): DocumentationCheckResult {
  const files: string[] = []
  const violations: DocumentationViolation[] = []

  for (const document of requiredDocuments) {
    const filePath = resolve(root, document)
    if (existsSync(filePath) && lstatSync(filePath).isFile()) {
      files.push(filePath)
    } else {
      violations.push(violation(filePath, undefined, 'required-document-exists', 'required documentation file does not exist'))
    }
  }

  for (const directory of documentationDirectories) {
    const directoryPath = resolve(root, directory)
    if (existsSync(directoryPath) && lstatSync(directoryPath).isDirectory()) {
      files.push(...collectMarkdownFiles(directoryPath))
    } else {
      violations.push(violation(directoryPath, undefined, 'documentation-root-exists', 'documentation directory does not exist'))
    }
  }

  return { files: files.sort(), violations }
}

function scanCharacters(
  filePath: string,
  node: MarkdownNode,
  value: string,
  pattern: RegExp,
  rule: string,
  describe: (character: string) => string,
): DocumentationViolation[] {
  const violations: DocumentationViolation[] = []
  const start = positionOf(node)
  const lines = value.split('\n')
  for (let lineIndex = 0; lineIndex < lines.length; lineIndex += 1) {
    pattern.lastIndex = 0
    const match = pattern.exec(lines[lineIndex])
    if (match === null) continue
    violations.push({
      filePath,
      line: start.line + lineIndex,
      column: lineIndex === 0 ? start.column + match.index : match.index + 1,
      rule,
      description: describe(match[0]),
    })
  }
  return violations
}

function proseCharacterViolations(filePath: string, tree: MarkdownNode): DocumentationViolation[] {
  const violations: DocumentationViolation[] = []
  walk(tree, (node) => {
    const proseValues: string[] = []
    if (node.type === 'text' && node.value !== undefined) proseValues.push(node.value)
    if ((node.type === 'image' || node.type === 'imageReference') && node.alt) proseValues.push(node.alt)
    if ((node.type === 'link' || node.type === 'image' || node.type === 'definition') && node.title) {
      proseValues.push(node.title)
    }

    for (const value of proseValues) {
      violations.push(...scanCharacters(
        filePath,
        node,
        value,
        nonLatinLetter,
        'latin-script-prose-only',
        (character) => `non-Latin letter is not allowed in prose: ${character}`,
      ))
    }
  })
  return violations
}

function rawHtmlViolations(filePath: string, tree: MarkdownNode): DocumentationViolation[] {
  const violations: DocumentationViolation[] = []
  walk(tree, (node) => {
    if (node.type !== 'html') return
    violations.push(violation(
      filePath,
      node,
      'raw-html-not-allowed',
      'raw HTML is not allowed in active documentation',
    ))
  })
  return violations
}

function normalizeFenceLanguage(language: string): string {
  let normalized = language.trim().toLowerCase()
  if (normalized.startsWith('{.')) normalized = normalized.slice(2)
  else if (normalized.startsWith('.')) normalized = normalized.slice(1)
  return normalized.split(/[\s}]/u, 1)[0] ?? ''
}

function diagramFenceViolations(filePath: string, tree: MarkdownNode): DocumentationViolation[] {
  const violations: DocumentationViolation[] = []
  walk(tree, (node) => {
    if (node.type !== 'code') return
    const normalizedLanguage = node.lang === null || node.lang === undefined
      ? ''
      : normalizeFenceLanguage(node.lang)

    if (forbiddenDiagramLanguages.has(normalizedLanguage)) {
      violations.push(violation(
        filePath,
        node,
        'ascii-diagram-only',
        `fenced ${node.lang} diagram must be replaced with a fenced \`text diagram\` ASCII diagram`,
      ))
      return
    }

    if (normalizedLanguage !== 'text') return

    const fenceKind = node.meta?.trim()
    if (fenceKind !== 'diagram' && fenceKind !== 'literal') {
      violations.push(violation(
        filePath,
        node,
        'text-fence-kind-required',
        'fenced text block must declare exactly `text diagram` or `text literal`',
      ))
      return
    }
    if (fenceKind === 'literal') return

    const match = nonAsciiCharacter.exec(node.value ?? '')
    if (match === null) return
    violations.push(violation(
      filePath,
      node,
      'ascii-diagram-only',
      `fenced text diagram contains a non-ASCII character: ${match[0]}`,
    ))
  })
  return violations
}

function headingFragments(tree: MarkdownNode): Set<string> {
  const fragments = new Set<string>()
  const slugger = new GithubSlugger()
  walk(tree, (node) => {
    if (node.type === 'heading') fragments.add(slugger.slug(toString(node)))
  })
  return fragments
}

function isWithinRepository(root: string, targetPath: string): boolean {
  const relativePath = relative(root, targetPath)
  return relativePath === ''
    || (relativePath !== '..' && !relativePath.startsWith(`..${sep}`) && !isAbsolute(relativePath))
}

function decodeUrlPart(value: string): string | undefined {
  try {
    return decodeURIComponent(value)
  } catch {
    return undefined
  }
}

function splitRelativeUrl(url: string): { path: string; fragment: string | undefined } | undefined {
  if (absoluteUrl.test(url) || url.startsWith('//') || url.startsWith('/')) return undefined

  const hashIndex = url.indexOf('#')
  const beforeFragment = hashIndex === -1 ? url : url.slice(0, hashIndex)
  const encodedFragment = hashIndex === -1 ? undefined : url.slice(hashIndex + 1)
  const queryIndex = beforeFragment.indexOf('?')
  const encodedPath = queryIndex === -1 ? beforeFragment : beforeFragment.slice(0, queryIndex)
  const path = decodeUrlPart(encodedPath)
  const fragment = encodedFragment === undefined ? undefined : decodeUrlPart(encodedFragment)
  if (path === undefined || fragment === undefined && encodedFragment !== undefined) return { path: '', fragment: undefined }
  return { path, fragment }
}

function linkOccurrences(tree: MarkdownNode): Array<{ node: MarkdownNode; url: string }> {
  const definitions = new Map<string, string>()
  walk(tree, (node) => {
    if (node.type === 'definition' && node.identifier !== undefined && node.url !== undefined) {
      if (!definitions.has(node.identifier)) definitions.set(node.identifier, node.url)
    }
  })

  const links: Array<{ node: MarkdownNode; url: string }> = []
  walk(tree, (node) => {
    if ((node.type === 'link' || node.type === 'image') && node.url !== undefined) {
      links.push({ node, url: node.url })
      return
    }
    if ((node.type === 'linkReference' || node.type === 'imageReference') && node.identifier !== undefined) {
      const url = definitions.get(node.identifier)
      if (url !== undefined) links.push({ node, url })
    }
  })
  return links
}

function markdownTargetPath(targetPath: string): string | undefined {
  const target = lstatSync(targetPath)
  if (target.isDirectory()) {
    const readmePath = resolve(targetPath, 'README.md')
    return existsSync(readmePath) && lstatSync(readmePath).isFile() ? readmePath : undefined
  }
  return target.isFile() && targetPath.toLowerCase().endsWith('.md') ? targetPath : undefined
}

function linkViolations(
  root: string,
  filePath: string,
  tree: MarkdownNode,
  headingCache: Map<string, Set<string>>,
): DocumentationViolation[] {
  const violations: DocumentationViolation[] = []

  for (const { node, url } of linkOccurrences(tree)) {
    const decoded = splitRelativeUrl(url)
    if (decoded === undefined) continue
    if (decoded.path === '' && url.includes('%') && decodeUrlPart(url.split(/[?#]/u)[0]) === undefined) {
      violations.push(violation(filePath, node, 'valid-relative-link', `relative link has invalid percent encoding: ${url}`))
      continue
    }
    if (url.includes('#') && decoded.fragment === undefined) {
      violations.push(violation(filePath, node, 'valid-relative-link', `relative link has invalid fragment encoding: ${url}`))
      continue
    }

    const targetPath = decoded.path === '' ? filePath : resolve(dirname(filePath), decoded.path)
    if (!isWithinRepository(root, targetPath)) {
      violations.push(violation(filePath, node, 'relative-link-target-exists', `relative link leaves the repository: ${url}`))
      continue
    }
    if (!existsSync(targetPath)) {
      violations.push(violation(filePath, node, 'relative-link-target-exists', `relative link target does not exist: ${url}`))
      continue
    }
    if (decoded.fragment === undefined || decoded.fragment === '') continue

    const markdownPath = markdownTargetPath(targetPath)
    if (markdownPath === undefined) {
      if (lstatSync(targetPath).isDirectory()) {
        violations.push(violation(
          filePath,
          node,
          'markdown-heading-fragment-exists',
          `directory link fragment has no README.md target: ${url}`,
        ))
      }
      continue
    }

    let headings = headingCache.get(markdownPath)
    if (headings === undefined) {
      const targetTree = markdownParser.parse(readFileSync(markdownPath, 'utf8')) as MarkdownNode
      headings = headingFragments(targetTree)
      headingCache.set(markdownPath, headings)
    }
    if (!headings.has(decoded.fragment)) {
      violations.push(violation(
        filePath,
        node,
        'markdown-heading-fragment-exists',
        `Markdown heading fragment does not exist: ${url}`,
      ))
    }
  }

  return violations
}

function undefinedReferenceViolations(
  filePath: string,
  source: string,
  tree: MarkdownNode,
): DocumentationViolation[] {
  const file = new VFile({ path: filePath, value: source })
  markdownParser.runSync(tree as Root, file)
  return file.messages.map((message) => ({
    filePath,
    line: message.line ?? 1,
    column: message.column ?? 1,
    rule: 'markdown-link-reference-exists',
    description: message.reason,
  }))
}

function compareViolations(left: DocumentationViolation, right: DocumentationViolation): number {
  const fileOrder = left.filePath < right.filePath ? -1 : left.filePath > right.filePath ? 1 : 0
  const ruleOrder = left.rule < right.rule ? -1 : left.rule > right.rule ? 1 : 0
  return fileOrder
    || left.line - right.line
    || left.column - right.column
    || ruleOrder
}

export function checkDocumentation(rootPath: string): DocumentationCheckResult {
  const root = resolve(rootPath)
  const discovered = findDocumentationFiles(root)
  const violations = [...discovered.violations]
  const headingCache = new Map<string, Set<string>>()

  for (const filePath of discovered.files) {
    const source = readFileSync(filePath, 'utf8')
    const tree = markdownParser.parse(source) as MarkdownNode
    const headings = headingFragments(tree)
    headingCache.set(filePath, headings)

    violations.push(...proseCharacterViolations(filePath, tree))
    violations.push(...rawHtmlViolations(filePath, tree))
    violations.push(...diagramFenceViolations(filePath, tree))
    violations.push(...undefinedReferenceViolations(filePath, source, tree))
    violations.push(...linkViolations(root, filePath, tree, headingCache))
  }

  return { files: discovered.files, violations: violations.sort(compareViolations) }
}

export function formatDocumentationViolation(rootPath: string, item: DocumentationViolation): string {
  const filePath = relative(resolve(rootPath), item.filePath).replaceAll('\\', '/')
  return `${filePath}:${item.line}:${item.column} ${item.rule}: ${item.description}`
}

function main(): void {
  const result = checkDocumentation(repositoryRoot)
  if (result.violations.length === 0) {
    console.log(`docs:check: checked ${result.files.length} active Markdown files`)
    return
  }

  for (const item of result.violations) console.error(formatDocumentationViolation(repositoryRoot, item))
  console.error(`docs:check: found ${result.violations.length} violation(s) in ${result.files.length} active Markdown files`)
  process.exitCode = 1
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main()
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error))
    process.exitCode = 1
  }
}
