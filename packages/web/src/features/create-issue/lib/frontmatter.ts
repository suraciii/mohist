export type IssueFrontmatter =
  | { kind: 'none' }
  | { kind: 'malformed' }
  | {
      kind: 'parsed'
      recommendedWorkflow?: string
      recommendedWorkflowReason?: string
      risk?: string
    }

const DELIMITER = '---'

function stripTrailing(line: string): string {
  return line.endsWith('\r') ? line.slice(0, -1) : line
}

function stripBom(line: string): string {
  return line.startsWith('\uFEFF') ? line.slice(1) : line
}

function isDelimiter(line: string): boolean {
  return line === DELIMITER
}

function leadingWhitespace(line: string): number {
  let count = 0
  for (const ch of line) {
    if (ch === ' ' || ch === '\t') count++
    else break
  }
  return count
}

function stripIndent(line: string, count: number): string {
  return line.length <= count ? '' : line.slice(count)
}

function unquote(value: string): string {
  if (value.length >= 2) {
    const first = value[0]
    const last = value[value.length - 1]
    if ((first === '"' && last === '"') || (first === "'" && last === "'")) {
      return value.slice(1, -1)
    }
  }
  return value
}

function nullIfEmpty(value: string): string | undefined {
  return value.length === 0 ? undefined : value
}

interface MutableIndex {
  i: number
}

function readBlock(lines: string[], index: MutableIndex, folded: boolean): string {
  const collected: string[] = []
  let indent = -1
  let k = index.i + 1
  let lastConsumed = index.i

  while (k < lines.length) {
    const line = stripTrailing(lines[k])
    if (line.length === 0) {
      collected.push('')
      lastConsumed = k
      k++
      continue
    }

    const leading = leadingWhitespace(line)
    if (leading === 0) break

    indent = indent < 0 ? leading : indent
    collected.push(stripIndent(line, indent))
    lastConsumed = k
    k++
  }

  while (collected.length > 0 && collected[collected.length - 1].length === 0) {
    collected.pop()
  }

  index.i = lastConsumed

  return folded ? collected.filter((l) => l.length > 0).join(' ') : collected.join('\n')
}

interface ParsedFields {
  recommendedWorkflow?: string
  recommendedWorkflowReason?: string
  risk?: string
  malformed: boolean
}

function parseFields(lines: string[]): ParsedFields {
  let recommendedWorkflow: string | undefined
  let recommendedWorkflowReason: string | undefined
  let risk: string | undefined

  for (let i = 0; i < lines.length; i++) {
    const raw = stripTrailing(lines[i])
    const trimmed = raw.trim()
    if (trimmed.length === 0 || trimmed[0] === '#') continue

    const colon = raw.indexOf(':')
    if (colon < 0) return { malformed: true }

    const key = raw.substring(0, colon).trim()
    if (key.length === 0) return { malformed: true }

    const rawValue = raw.substring(colon + 1).trim()
    const index: MutableIndex = { i }
    const value =
      rawValue === '|' || rawValue === '>'
        ? readBlock(lines, index, rawValue === '>')
        : unquote(rawValue)
    i = index.i

    switch (key) {
      case 'recommended_workflow':
        recommendedWorkflow = nullIfEmpty(value)
        break
      case 'recommended_workflow_reason':
        recommendedWorkflowReason = nullIfEmpty(value)
        break
      case 'risk':
        risk = nullIfEmpty(value)
        break
    }
  }

  return { recommendedWorkflow, recommendedWorkflowReason, risk, malformed: false }
}

function findClosingDelimiter(lines: string[]): number {
  for (let i = 1; i < lines.length; i++) {
    if (isDelimiter(stripTrailing(lines[i]))) return i
  }
  return -1
}

export function parseIssueFrontmatter(text: string): IssueFrontmatter {
  if (!text) return { kind: 'none' }

  const lines = text.split('\n')
  if (!isDelimiter(stripTrailing(stripBom(lines[0])))) {
    return { kind: 'none' }
  }

  const closingIndex = findClosingDelimiter(lines)
  if (closingIndex === -1) return { kind: 'malformed' }

  const frontmatter = lines.slice(1, closingIndex)
  const fields = parseFields(frontmatter)
  if (fields.malformed) return { kind: 'malformed' }

  return {
    kind: 'parsed',
    recommendedWorkflow: fields.recommendedWorkflow,
    recommendedWorkflowReason: fields.recommendedWorkflowReason,
    risk: fields.risk,
  }
}
