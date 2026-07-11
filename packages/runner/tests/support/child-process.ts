import type { ChildProcess } from "node:child_process"
import { killProcess } from "../../src/system/process.js"

const registeredChildren = new Set<ChildProcess>()

export function registerTestChild(child: ChildProcess): void {
  registeredChildren.add(child)
  child.once("close", () => registeredChildren.delete(child))
}

export async function cleanupRegisteredChildren(): Promise<void> {
  const children = [...registeredChildren]
  for (const child of children) {
    if (child.exitCode !== null || child.signalCode !== null) continue
    killProcess(child, "SIGKILL")
  }

  await Promise.all(children.map(waitForChildClose))
  if (registeredChildren.size > 0) throw new Error("Integration test left external child processes running")
}

function waitForChildClose(child: ChildProcess): Promise<void> {
  return new Promise((resolve) => {
    const onClose = () => resolve()
    child.once("close", onClose)
    if (!registeredChildren.has(child)) {
      child.off("close", onClose)
      resolve()
    }
  })
}
