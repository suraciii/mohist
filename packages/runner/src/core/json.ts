import type { JsonObject, JsonValue } from "./types.js"

export function parseObject(value?: string | null) {
  if (!value?.trim()) return null
  return JSON.parse(value) as JsonObject
}

export function parseArray(value?: string | null): JsonValue[] | null {
  if (!value?.trim()) return null
  const parsed = JSON.parse(value) as JsonValue
  return Array.isArray(parsed) ? parsed : null
}

export function parseTaskOutputs(value?: string | null): { name: string; from: string }[] | null {
  const arr = parseArray(value)
  if (!arr) return null
  const outputs: { name: string; from: string }[] = []
  for (const item of arr) {
    if (!isObject(item)) continue
    const name = item.name
    const from = item.from
    if (typeof name === "string" && typeof from === "string") {
      outputs.push({ name, from })
    }
  }
  return outputs.length > 0 ? outputs : null
}

export function stringInput(input: JsonObject | null | undefined, key: string) {
  const value = input?.[key]
  if (value === undefined || value === null) return undefined
  return typeof value === "string" ? value : JSON.stringify(value)
}

export function numberInput(input: JsonObject | null | undefined, key: string) {
  const value = input?.[key]
  if (typeof value === "number") return value
  if (typeof value !== "string") return undefined
  const parsed = Number(value)
  return Number.isFinite(parsed) ? parsed : undefined
}

export function objectInput(input: JsonObject | null | undefined, key: string) {
  const value = input?.[key]
  return isObject(value) ? value : undefined
}

export function arrayInput(input: JsonObject | null | undefined, key: string) {
  const value = input?.[key]
  return Array.isArray(value) ? value : []
}

export function isObject(value: JsonValue | undefined): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value)
}
