/** Pure helpers over pi session message state shared by turn settlement paths. */
export function finalText(messages: readonly { role?: string; content?: unknown }[]): string | null {
  const assistant = [...messages].reverse().find((item) => item.role === 'assistant')
  return contentText(assistant?.content)
}
function contentText(content: unknown): string | null {
  if (typeof content === 'string') return content
  if (!Array.isArray(content)) return null
  const text = content
    .map((part) =>
      typeof part === 'string'
        ? part
        : part && typeof part === 'object' && 'text' in part && typeof part.text === 'string'
          ? part.text
          : '',
    )
    .join('')
  return text || null
}
