export class RateLimitHandler {
  private static instance: RateLimitHandler;
  private requestCount: number = 0;
  private resetTime: number = 0;
  private remaining: number = 5000;

  static getInstance(): RateLimitHandler {
    if (!RateLimitHandler.instance) {
      RateLimitHandler.instance = new RateLimitHandler();
    }
    return RateLimitHandler.instance;
  }

  async waitForReset(): Promise<void> {
    if (this.remaining <= 0) {
      const waitTime = this.resetTime - Date.now();
      if (waitTime > 0) {
        console.log(`Rate limit reached. Waiting ${Math.ceil(waitTime / 1000)}s...`);
        await new Promise(resolve => setTimeout(resolve, waitTime));
      }
    }
  }

  updateLimits(remaining: number, resetTime: number): void {
    this.remaining = remaining;
    this.resetTime = resetTime * 1000;
  }

  getRemaining(): number {
    return this.remaining;
  }

  getResetTime(): number {
    return this.resetTime;
  }

  incrementRequest(): void {
    this.requestCount++;
  }

  getRequestCount(): number {
    return this.requestCount;
  }
}
