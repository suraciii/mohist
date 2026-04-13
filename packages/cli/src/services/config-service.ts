import { Config } from '../types';
import { ConfigRepo, initializeDefaultConfig } from '../db';

export class ConfigService {
  private static KEYS = {
    AGENT_TIMEOUT: 'agent.timeout',
    AGENT_MAX_CONCURRENT: 'agent.maxConcurrent',
    POLL_INTERVAL: 'poll.interval',
  };

  constructor(private configRepo: ConfigRepo) {
    initializeDefaultConfig(configRepo);
  }

  get(key: string): string | null {
    return this.configRepo.get(key);
  }

  set(key: string, value: string | number | boolean): void {
    this.configRepo.set(key, value);
  }

  delete(key: string): boolean {
    return this.configRepo.delete(key);
  }

  getAll(): Record<string, string> {
    return this.configRepo.getAll();
  }

  getConfig(): Omit<Config, 'serverPort' | 'serverHost'> {
    return {
      agentTimeout: this.configRepo.getNumber(ConfigService.KEYS.AGENT_TIMEOUT, 1800000),
      maxConcurrentAgents: this.configRepo.getNumber(ConfigService.KEYS.AGENT_MAX_CONCURRENT, 8),
      pollInterval: this.configRepo.getNumber(ConfigService.KEYS.POLL_INTERVAL, 30000),
    };
  }

  getAgentTimeout(): number {
    return this.configRepo.getNumber(ConfigService.KEYS.AGENT_TIMEOUT, 1800000);
  }

  setAgentTimeout(timeoutMs: number): void {
    this.configRepo.set(ConfigService.KEYS.AGENT_TIMEOUT, timeoutMs);
  }

  getMaxConcurrentAgents(): number {
    return this.configRepo.getNumber(ConfigService.KEYS.AGENT_MAX_CONCURRENT, 8);
  }

  setMaxConcurrentAgents(max: number): void {
    this.configRepo.set(ConfigService.KEYS.AGENT_MAX_CONCURRENT, max);
  }

  getPollInterval(): number {
    return this.configRepo.getNumber(ConfigService.KEYS.POLL_INTERVAL, 30000);
  }

  setPollInterval(intervalMs: number): void {
    this.configRepo.set(ConfigService.KEYS.POLL_INTERVAL, intervalMs);
  }

  resetToDefaults(): void {
    this.configRepo.clear();
    initializeDefaultConfig(this.configRepo);
  }

  validate(key: string, value: string): { valid: boolean; error?: string } {
    switch (key) {
      case ConfigService.KEYS.AGENT_TIMEOUT:
        const timeout = parseInt(value, 10);
        if (isNaN(timeout) || timeout < 60000) {
          return { valid: false, error: 'Timeout must be at least 60000ms (1 minute)' };
        }
        break;
      
      case ConfigService.KEYS.AGENT_MAX_CONCURRENT:
        const maxConcurrent = parseInt(value, 10);
        if (isNaN(maxConcurrent) || maxConcurrent < 1 || maxConcurrent > 16) {
          return { valid: false, error: 'Max concurrent agents must be between 1 and 16' };
        }
        break;
      
      case ConfigService.KEYS.POLL_INTERVAL:
        const interval = parseInt(value, 10);
        if (isNaN(interval) || interval < 5000) {
          return { valid: false, error: 'Poll interval must be at least 5000ms (5 seconds)' };
        }
        break;
    }
    
    return { valid: true };
  }
}
