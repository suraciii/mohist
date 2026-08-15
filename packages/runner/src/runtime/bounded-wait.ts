/**
 * Wait for an asynchronous cleanup operation without allowing it to hold the
 * caller forever. The operation is deliberately abandoned after the bound;
 * its outcome is observed so a late rejection cannot become unhandled.
 */
export async function boundedWait(operation: () => void | PromiseLike<unknown>, timeoutMs: number): Promise<boolean> {
  let timer: ReturnType<typeof setTimeout> | undefined
  const pending = Promise.resolve()
    .then(operation)
    .then(
      () => true,
      () => true,
    )
  const timeout = new Promise<boolean>((resolve) => {
    timer = setTimeout(() => resolve(false), timeoutMs)
    timer.unref?.()
  })
  try {
    return await Promise.race([pending, timeout])
  } finally {
    if (timer !== undefined) clearTimeout(timer)
  }
}

export function boundedTimeoutMs(value: number | undefined, fallback: number): number {
  return value !== undefined && Number.isFinite(value) && value >= 0 ? Math.floor(value) : fallback
}
