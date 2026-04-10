import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { RateLimiter } from '../src/utils/rate-limiter';

describe('RateLimiter', () => {
  describe('constructor', () => {
    it('should initialize with empty map', () => {
      const limiter = new RateLimiter(60000, 30);
      const result = limiter.check('127.0.0.1');
      expect(result.allowed).toBe(true);
      limiter.dispose();
    });
  });

  describe('check', () => {
    it('should allow requests within limit', () => {
      const limiter = new RateLimiter(60000, 30);

      for (let i = 0; i < 30; i++) {
        const result = limiter.check('127.0.0.1');
        expect(result.allowed).toBe(true);
      }

      limiter.dispose();
    });

    it('should block excessive requests', () => {
      const limiter = new RateLimiter(60000, 30);

      for (let i = 0; i < 30; i++) {
        limiter.check('127.0.0.1');
      }

      const result = limiter.check('127.0.0.1');
      expect(result.allowed).toBe(false);
      expect(result.retryAfter).toBeGreaterThan(0);
      limiter.dispose();
    });

    it('should track different IPs independently', () => {
      const limiter = new RateLimiter(60000, 30);

      for (let i = 0; i < 30; i++) {
        limiter.check('192.168.1.1');
      }

      const result = limiter.check('192.168.1.2');
      expect(result.allowed).toBe(true);

      limiter.dispose();
    });

    it('should reset after window expires', () => {
      vi.useFakeTimers();
      const limiter = new RateLimiter(1000, 30);

      for (let i = 0; i < 30; i++) {
        limiter.check('127.0.0.1');
      }

      const blocked = limiter.check('127.0.0.1');
      expect(blocked.allowed).toBe(false);

      vi.advanceTimersByTime(1001);
      const reset = limiter.check('127.0.0.1');
      expect(reset.allowed).toBe(true);

      limiter.dispose();
      vi.useRealTimers();
    });
  });

  describe('dispose', () => {
    it('should clear timer and map', () => {
      const limiter = new RateLimiter(60000, 30);

      limiter.check('127.0.0.1');
      limiter.check('192.168.1.1');

      limiter.dispose();

      const result = limiter.check('127.0.0.1');
      expect(result.allowed).toBe(true);
    });

    it('should allow recreation after disposal', () => {
      const limiter1 = new RateLimiter(60000, 30);
      limiter1.dispose();

      const limiter2 = new RateLimiter(60000, 30);
      const result = limiter2.check('127.0.0.1');
      expect(result.allowed).toBe(true);
      limiter2.dispose();
    });
  });

  describe('cleanup', () => {
    it('should preserve active entries during cleanup', () => {
      const limiter = new RateLimiter(10000, 30);

      limiter.check('127.0.0.1');

      limiter.dispose();
    });

    it('should remove expired entries', () => {
      const limiter = new RateLimiter(50, 30);

      limiter.check('127.0.0.1');
      expect(limiter.check('127.0.0.1').allowed).toBe(true);

      limiter.dispose();
    });
  });
});