export interface RateLimitRecord {
  count: number;
  resetAt: number;
}

export interface CheckResult {
  allowed: boolean;
  retryAfter?: number;
}

export class RateLimiter {
  private map = new Map<string, RateLimitRecord>();
  private timer: NodeJS.Timeout | null = null;

  constructor(private windowMs: number, private maxRequests: number) {
    this.timer = setInterval(() => this.cleanup(), this.windowMs);
  }

  check(ip: string): CheckResult {
    const now = Date.now();
    const record = this.map.get(ip);

    if (record && record.resetAt > now) {
      record.count++;
      if (record.count > this.maxRequests) {
        const retryAfter = Math.ceil((record.resetAt - now) / 1000);
        return { allowed: false, retryAfter };
      }
      return { allowed: true };
    }

    this.map.set(ip, { count: 1, resetAt: now + this.windowMs });
    return { allowed: true };
  }

  dispose(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
    this.map.clear();
  }

  private cleanup(): void {
    const now = Date.now();
    for (const [ip, record] of this.map.entries()) {
      if (record.resetAt <= now) {
        this.map.delete(ip);
      }
    }
  }
}