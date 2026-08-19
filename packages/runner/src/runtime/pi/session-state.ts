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
export function lastMessageFailed(messages: readonly { role?: string; stopReason?: string }[]): boolean {
  const item = [...messages].reverse().find((entry) => entry.role === 'assistant')
  return item?.stopReason === 'error'
}
export function lastMessageError(messages: readonly { role?: string; errorMessage?: string }[]): string | undefined {
  return [...messages].reverse().find((entry) => entry.role === 'assistant')?.errorMessage
}
