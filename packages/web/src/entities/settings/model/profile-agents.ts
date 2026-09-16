/**
 * Named Agents referenced by a Workflow Profile Definition.
 *
 * The Profile detail exposes the raw DefinitionSource, so the scan reads the
 * literal `with.name` of every `mohist/agent` task. Other `with` keys and
 * non-Agent actions are irrelevant to the execution authority question: the
 * named Agent owns Runtime, Model, Reasoning Effort, and Variant.
 */

const USES_PATTERN = /^(\s*)(?:-\s+)?uses:\s*mohist\/agent\s*(?:#.*)?$/
const WITH_PATTERN = /^(\s*)with:\s*(.*)$/
const NAME_PATTERN = /^(\s*)name:\s*(.+?)\s*$/

function indentOf(line: string): number {
  return line.length - line.trimStart().length
}

function literalValue(raw: string): string | null {
  const withoutComment = raw.replace(/\s+#.*$/, '').trim()
  if (!withoutComment) return null
  const quoted =
    (withoutComment.startsWith('"') && withoutComment.endsWith('"')) ||
    (withoutComment.startsWith("'") && withoutComment.endsWith("'"))
  const value = quoted ? withoutComment.slice(1, -1) : withoutComment
  return value.trim() || null
}

function inlineNameOf(withValue: string): string | null {
  const match = /(?:\{|,)\s*name:\s*([^,}]+)/.exec(withValue)
  return match ? literalValue(match[1]) : null
}

function findAgentName(lines: string[], usesIndex: number, usesIndent: number): string | null {
  for (let index = usesIndex + 1; index < lines.length; index++) {
    const line = lines[index]
    if (!line.trim()) continue

    const indent = indentOf(line)
    // A shallower line ends this task; a sibling sequence item starts the next one.
    if (indent < usesIndent) return null
    if (indent <= usesIndent && line.trimStart().startsWith('- ')) return null

    const withMatch = WITH_PATTERN.exec(line)
    if (!withMatch) continue

    const inline = withMatch[2].trim()
    if (inline.startsWith('{')) return inlineNameOf(inline)

    const withIndent = withMatch[1].length
    for (let nameIndex = index + 1; nameIndex < lines.length; nameIndex++) {
      const nameLine = lines[nameIndex]
      if (!nameLine.trim()) continue
      const nameIndent = indentOf(nameLine)
      if (nameIndent <= withIndent || nameLine.trimStart().startsWith('- ')) return null
      const nameMatch = NAME_PATTERN.exec(nameLine)
      if (nameMatch) return literalValue(nameMatch[2])
    }
    return null
  }
  return null
}

/**
 * Returns the named Agents a Profile Definition runs through `mohist/agent`
 * tasks, in definition order and without duplicates.
 */
export function parseProfileAgentNames(definitionSource: string | null | undefined): string[] {
  if (!definitionSource) return []

  const lines = definitionSource.split(/\r?\n/)
  const names: string[] = []
  const seen = new Set<string>()
  for (let index = 0; index < lines.length; index++) {
    const usesMatch = USES_PATTERN.exec(lines[index])
    if (!usesMatch) continue
    const name = findAgentName(lines, index, usesMatch[1].length)
    if (!name || seen.has(name)) continue
    seen.add(name)
    names.push(name)
  }
  return names
}
