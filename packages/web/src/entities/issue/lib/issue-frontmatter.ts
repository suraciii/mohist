export type IssueFrontmatterKind = 'none' | 'closed' | 'unclosed'

export interface IssueBodyPartition {
  kind: IssueFrontmatterKind
  recommendedWorkflow?: string
  recommendedWorkflowReason?: string
  risk?: string
  description: string
  rawEnvelope: string
}

const DELIMITER = '---'

interface BodyLine {
  content: string
  start: number
  end: number
}

interface RecognizedMetadata {
  recommendedWorkflow?: string
  recommendedWorkflowReason?: string
  risk?: string
}

function splitLines(body: string): BodyLine[] {
  const lines: BodyLine[] = []
  let start = 0

  while (start < body.length) {
    const newline = body.indexOf('\n', start)
    if (newline < 0) {
      const contentEnd = body.endsWith('\r') ? body.length - 1 : body.length
      lines.push({ content: body.slice(start, contentEnd), start, end: body.length })
      break
    }

    const contentEnd = newline > start && body[newline - 1] === '\r' ? newline - 1 : newline
    lines.push({ content: body.slice(start, contentEnd), start, end: newline + 1 })
    start = newline + 1
  }

  return lines
}

function leadingWhitespace(value: string): number {
  let count = 0
  while (value[count] === ' ' || value[count] === '\t') count++
  return count
}

function unquote(value: string): string {
  if (value.length < 2) return value
  const first = value[0]
  const last = value[value.length - 1]
  return first === last && (first === '"' || first === "'") ? value.slice(1, -1) : value
}

function optionalValue(value: string): string | undefined {
  return value.length > 0 ? value : undefined
}

function readBlock(lines: BodyLine[], index: number, folded: boolean): [string, number] {
  const values: string[] = []
  let indent = -1
  let lastIndex = index

  for (let current = index + 1; current < lines.length; current++) {
    const line = lines[current].content
    if (line.length === 0) {
      values.push('')
      lastIndex = current
      continue
    }

    const whitespace = leadingWhitespace(line)
    if (whitespace === 0) break
    if (indent < 0) indent = whitespace
    values.push(line.slice(indent))
    lastIndex = current
  }

  while (values.at(-1) === '') values.pop()
  return [folded ? values.filter(Boolean).join(' ') : values.join('\n'), lastIndex]
}

function parseMetadata(lines: BodyLine[]): RecognizedMetadata | null {
  const metadata: RecognizedMetadata = {}

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index].content
    const trimmed = line.trim()
    if (!trimmed || trimmed.startsWith('#')) continue

    const colon = line.indexOf(':')
    if (colon < 0 || !line.slice(0, colon).trim()) return null

    const key = line.slice(0, colon).trim()
    const rawValue = line.slice(colon + 1).trim()
    let value: string
    if (rawValue === '|' || rawValue === '>') {
      const block = readBlock(lines, index, rawValue === '>')
      value = block[0]
      index = block[1]
    } else {
      value = unquote(rawValue)
    }

    switch (key) {
      case 'recommended_workflow':
        metadata.recommendedWorkflow = optionalValue(value)
        break
      case 'recommended_workflow_reason':
        metadata.recommendedWorkflowReason = optionalValue(value)
        break
      case 'risk':
        metadata.risk = optionalValue(value)
        break
    }
  }

  return metadata
}

function lineEnding(rawEnvelope: string): string {
  return rawEnvelope.includes('\r\n') ? '\r\n' : '\n'
}

export function partitionIssueBody(body: string | null | undefined): IssueBodyPartition {
  if (!body) return { kind: 'none', description: '', rawEnvelope: '' }

  const lines = splitLines(body)
  const opening = lines[0]?.content.replace(/^\uFEFF/, '')
  if (opening !== DELIMITER) {
    return { kind: 'none', description: body, rawEnvelope: '' }
  }

  const closingIndex = lines.findIndex((line, index) => index > 0 && line.content === DELIMITER)
  if (closingIndex < 0) {
    return { kind: 'unclosed', description: '', rawEnvelope: body }
  }

  const envelopeEnd = lines[closingIndex].end
  const metadata = parseMetadata(lines.slice(1, closingIndex))
  return {
    kind: 'closed',
    ...(metadata ?? {}),
    description: body.slice(envelopeEnd),
    rawEnvelope: body.slice(0, envelopeEnd),
  }
}

export function recombineIssueBody(partition: IssueBodyPartition, description: string): string {
  if (partition.kind === 'none') return description

  const ending = lineEnding(partition.rawEnvelope)
  if (partition.kind === 'closed') {
    const separator = description && !partition.rawEnvelope.endsWith('\n') ? ending : ''
    return `${partition.rawEnvelope}${separator}${description}`
  }

  const beforeDelimiter = partition.rawEnvelope.endsWith('\n') ? '' : ending
  const afterDelimiter = description ? ending : ''
  return `${partition.rawEnvelope}${beforeDelimiter}${DELIMITER}${afterDelimiter}${description}`
}
