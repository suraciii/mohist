export interface TaskLogLine {
  seq: number
  timestamp: string
  source: string
  text: string
}

export interface TaskLogPage {
  lines: TaskLogLine[]
  nextCursor: number | null
  truncated: boolean
}