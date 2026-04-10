import { describe, it, expect } from 'vitest';
import { maskSensitiveData } from '../src/utils/sensitive-data';

describe('maskSensitiveData', () => {
  describe('apiKey field', () => {
    it('should mask apiKey field', () => {
      const input = { apiKey: 'sk-test-api-key-12345' };
      const result = maskSensitiveData(input);
      expect(result.apiKey).toBe('sk-t**************345');
    });

    it('should mask short apiKey with asterisks', () => {
      const input = { apiKey: 'short' };
      const result = maskSensitiveData(input);
      expect(result.apiKey).toBe('********');
    });
  });

  describe('secret field', () => {
    it('should mask secret field', () => {
      const input = { secret: 'super-secret-value-abcdef' };
      const result = maskSensitiveData(input);
      expect(result.secret).toBe('supe******************def');
    });
  });

  describe('token field', () => {
    it('should mask token field', () => {
      const input = { token: 'ghp_token1234567890abcdef' };
      const result = maskSensitiveData(input);
      expect(result.token).toBe('ghp_******************def');
    });
  });

  describe('nested objects', () => {
    it('should mask sensitive fields in nested objects', () => {
      const input = {
        provider: 'openai',
        config: {
          apiKey: 'sk-nested-key-12345678',
        },
      };
      const result = maskSensitiveData(input);
      expect((result as any).config.apiKey).toBe('sk-n***************678');
    });

    it('should mask deeply nested sensitive fields', () => {
      const input = {
        level1: {
          level2: {
            level3: {
              apiKey: 'sk-deep-key-abcdefgh',
            },
          },
        },
      };
      const result = maskSensitiveData(input);
      expect((result as any).level1.level2.level3.apiKey).toBe('sk-d*************fgh');
    });
  });

  describe('arrays', () => {
    it('should mask sensitive fields in arrays', () => {
      const input = {
        providers: [
          { id: 'openai', apiKey: 'sk-array-key-one-1234' },
          { id: 'anthropic', apiKey: 'sk-array-key-two-5678' },
        ],
      };
      const result = maskSensitiveData(input) as any;
      expect(result.providers[0].apiKey).toBe('sk-a**************234');
      expect(result.providers[1].apiKey).toBe('sk-a**************678');
    });
  });

  describe('original object immutability', () => {
    it('should not modify the original object', () => {
      const input = { apiKey: 'sk-original-key-abcdef' };
      maskSensitiveData(input);
      expect(input.apiKey).toBe('sk-original-key-abcdef');
    });

    it('should not modify nested objects in original', () => {
      const input = {
        config: {
          apiKey: 'sk-nested-original-12345',
        },
      };
      maskSensitiveData(input);
      expect((input as any).config.apiKey).toBe('sk-nested-original-12345');
    });
  });

  describe('non-sensitive fields', () => {
    it('should preserve non-sensitive fields', () => {
      const input = {
        id: 'openai',
        baseURL: 'https://api.openai.com',
        enabled: true,
        timeout: 30000,
      };
      const result = maskSensitiveData(input) as any;
      expect(result.id).toBe('openai');
      expect(result.baseURL).toBe('https://api.openai.com');
      expect(result.enabled).toBe(true);
      expect(result.timeout).toBe(30000);
    });
  });

  describe('mixed sensitive and non-sensitive', () => {
    it('should mask only sensitive fields', () => {
      const input = {
        name: 'OpenAI Provider',
        apiKey: 'sk-mixed-key-abcdefgh',
        baseURL: 'https://api.openai.com',
        secret: 'my-secret-value-12345',
        enabled: true,
      };
      const result = maskSensitiveData(input) as any;
      expect(result.name).toBe('OpenAI Provider');
      expect(result.apiKey).toBe('sk-m**************fgh');
      expect(result.baseURL).toBe('https://api.openai.com');
      expect(result.secret).toBe('my-s**************345');
      expect(result.enabled).toBe(true);
    });
  });

  describe('empty and null values', () => {
    it('should handle empty object', () => {
      const input = {};
      const result = maskSensitiveData(input);
      expect(result).toEqual({});
    });

    it('should handle object with only null values', () => {
      const input = { apiKey: null, data: null };
      const result = maskSensitiveData(input) as any;
      expect(result.apiKey).toBe(null);
      expect(result.data).toBe(null);
    });
  });
});