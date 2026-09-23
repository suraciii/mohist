import type { CodexTurnTransport } from './turn.js'

export interface CodexTurnMessageCapture {
  replayTo(listener: (message: unknown) => void): void
}

export async function withCapturedTurnMessages<T>(
  transport: CodexTurnTransport,
  work: (capture: CodexTurnMessageCapture) => Promise<T>,
): Promise<T> {
  const messages: unknown[] = []
  let overflowed = false
  const unsubscribe = transport.subscribe((message) => {
    if (overflowed) return
    if (messages.length < 1024) messages.push(message)
    else {
      messages.length = 0
      overflowed = true
    }
  })
  let active = true
  try {
    return await work({
      replayTo(listener) {
        if (!active) return
        active = false
        unsubscribe()
        try {
          if (overflowed) {
            listener({
              method: 'protocol-failure',
              params: { message: 'Codex Turn submission event buffer overflowed' },
            })
          } else {
            for (const message of messages) listener(message)
          }
        } finally {
          messages.length = 0
        }
      },
    })
  } finally {
    if (active) unsubscribe()
    messages.length = 0
  }
}
