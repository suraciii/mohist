export interface QuietThresholdMonitor {
  start(): void;
  restart(): void;
  clear(): void;
  promise(): Promise<'quiet_threshold'>;
}

export function createQuietThresholdMonitor(thresholdMs: number): QuietThresholdMonitor {
  let timer: ReturnType<typeof setTimeout> | null = null;
  let settled = false;
  let resolveFn: ((v: 'quiet_threshold') => void) | null = null;
  const thePromise = new Promise<'quiet_threshold'>((resolve) => { resolveFn = resolve; });

  const fire = () => {
    timer = null;
    settled = true;
    resolveFn?.('quiet_threshold');
  };

  const start = () => {
    if (timer) return;
    settled = false;
    timer = setTimeout(fire, thresholdMs);
  };

  const restart = () => {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    start();
  };

  const clear = () => {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
  };

  const promise = () => settled ? Promise.resolve('quiet_threshold' as const) : thePromise;

  return { start, restart, clear, promise };
}

export function createQuietThresholdMonitorForTest(thresholdMs: number): QuietThresholdMonitor {
  return createQuietThresholdMonitor(thresholdMs);
}
