/**
 * Markdown scaffold for the Epic description field.
 *
 * Mirrors the {@link composeIssueTemplateBody} convention used by
 * `CreateIssueDialog`: each section is a `## Title` heading followed by a single
 * placeholder line, and the sections are joined with a blank line. The result is
 * a free-form markdown string the user can edit, reorder, or replace wholesale.
 *
 * The scaffold is a starting point only — the Create/Edit dialogs are free to
 * send any markdown string the user authors; no API field changes.
 */
export const EPIC_DESCRIPTION_TEMPLATE = [
  '## Goal',
  '<what this epic is trying to achieve>',
  '',
  '## Background',
  '<why this matters now and what context the team needs>',
  '',
  '## Non-goals',
  '<what is explicitly out of scope for this epic>',
  '',
  '## Scope',
  '<which areas, issues, or systems are in play>',
].join('\n')

const REQUIRED_HEADERS = ['## Goal', '## Background', '## Non-goals', '## Scope'] as const

function hasStandaloneHeader(content: string, header: string): boolean {
  const escapedHeader = header.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  return new RegExp(`^${escapedHeader}\\s*$`, 'm').test(content)
}

/**
 * Conservative detector: returns true only when all four required section
 * headers are present in the description. Used by Create/Edit dialogs to decide
 * whether to auto-prefill the scaffold (Create, empty) versus offering an
 * opt-in Insert action (Edit, or Create with non-empty text).
 *
 * The detector is intentionally strict: it does NOT rewrite or strip content,
 * and a "false" answer never causes user text to be destroyed.
 */
export function hasEpicDescriptionStructure(content: string | null | undefined): boolean {
  if (!content) return false
  for (const header of REQUIRED_HEADERS) {
    if (!hasStandaloneHeader(content, header)) return false
  }
  return true
}
