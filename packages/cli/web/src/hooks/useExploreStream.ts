import { useState, useCallback, useRef } from 'react'
import type { ToolCallRecord } from '../lib/types'

interface StreamEvent {
  type: 'tool_call' | 'chunk' | 'done'
  tool?: string
  args?: Record<string, unknown>
  result?: unknown
  content?: string
  issueId?: string | null
  error?: string
}

interface UseExploreStreamReturn {
  streaming: boolean
  streamContent: string
  streamToolCalls: ToolCallRecord[]
  streamIssueId: string | null
  send: (sessionId: string, content: string) => Promise<void>
  reset: () => void
}

export function useExploreStream(): UseExploreStreamReturn {
  const [streaming, setStreaming] = useState(false)
  const [streamContent, setStreamContent] = useState('')
  const [streamToolCalls, setStreamToolCalls] = useState<ToolCallRecord[]>([])
  const [streamIssueId, setStreamIssueId] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  const reset = useCallback(() => {
    setStreamContent('')
    setStreamToolCalls([])
    setStreamIssueId(null)
  }, [])

  const send = useCallback(async (sessionId: string, content: string) => {
    abortRef.current?.abort()
    const controller = new AbortController()
    abortRef.current = controller

    setStreaming(true)
    reset()
    setStreamContent('')

    try {
      const res = await fetch(`/api/explore/${encodeURIComponent(sessionId)}/messages`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ content }),
        signal: controller.signal,
      })

      if (!res.ok) {
        const json = await res.json()
        throw new Error(json.error || `Request failed: ${res.status}`)
      }

      const reader = res.body?.getReader()
      if (!reader) throw new Error('No response body')

      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break

        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() || ''

        for (const line of lines) {
          const trimmed = line.trim()
          if (!trimmed || !trimmed.startsWith('data: ')) continue
          const jsonStr = trimmed.slice(6)
          if (jsonStr === '[DONE]') continue

          try {
            const event: StreamEvent = JSON.parse(jsonStr)
            if (event.type === 'chunk') {
              setStreamContent((prev) => prev + (event.content || ''))
            } else if (event.type === 'tool_call') {
              setStreamToolCalls((prev) => [
                ...prev,
                {
                  name: event.tool || 'unknown',
                  args: (event.args as Record<string, unknown>) || {},
                  result: event.result,
                },
              ])
            } else if (event.type === 'done') {
              if (event.issueId) {
                setStreamIssueId(event.issueId)
              }
            }
          } catch {
            // skip malformed JSON
          }
        }
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return
      console.error('[explore] Stream error:', err)
    } finally {
      setStreaming(false)
      abortRef.current = null
    }
  }, [reset])

  return { streaming, streamContent, streamToolCalls, streamIssueId, send, reset }
}
