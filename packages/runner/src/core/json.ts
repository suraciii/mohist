import type { JsonObject, JsonValue } from "./types.js"

export function parseObject(value?: string | null) {
  if (!value?.trim()) return null
  return JSON.parse(value) as JsonObject
}

export function stringInput(input: JsonObject | null | undefined, key: string) {
  const value = input?.[key]
  if (value === undefined || value === null) return undefined
  return typeof value === "string" ? value : JSON.stringify(value)
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
