import { Config } from '../types';
import { ConfigRepo, initializeDefaultConfig } from '../db';
import {
  load,
  writeConfig,
  getAgentTimeoutConfig,
} from '../config/config-loader';
import type { ConfigInfo } from '../config/config-schema';

const TIMEOUT_KEYS = new Set([
  'agent.taskTimeout',
  'agent.stageTimeout',
  'agent.maxGracePeriods',
]);

function setNestedValue(config: ConfigInfo, key: string, value: unknown): ConfigInfo {
  const updated = { ...config };
  if (key === 'agent.taskTimeout') {
    updated.agent = { ...updated.agent, taskTimeout: value as number };
  } else if (key === 'agent.stageTimeout') {
    updated.agent = { ...updated.agent, stageTimeout: value as number };
  } else if (key === 'agent.maxGracePeriods') {
    updated.agent = { ...updated.agent, maxGracePeriods: value as number };
  }
  return updated;
}

export class ConfigService {
  private static KEYS = {
    AGENT_TIMEOUT: 'agent.timeout',
    AGENT_MAX_CONCURRENT: 'agent.maxConcurrent',
    POLL_INTERVAL: 'poll.interval',
    AGENT_TASK_TIMEOUT: 'agent.taskTimeout',
    AGENT_STAGE_TIMEOUT: 'agent.stageTimeout',
    AGENT_MAX_GRACE_PERIODS: 'agent.maxGracePeriods',
  };

  constructor(private configRepo: ConfigRepo) {
    initializeDefaultConfig(configRepo);
  }

  get(key: string): string | null {
    return this.configRepo.get(key);
  }

  set(key: string, value: string | number | boolean): void {
    if (TIMEOUT_KEYS.has(key)) {
      const numValue = typeof value === 'number' ? value : Number(value);
      const current = load();
      const updated = setNestedValue(current, key, numValue);
      writeConfig(updated);
      return;
    }
    this.configRepo.set(key, value);
  }

  delete(key: string): boolean {
    return this.configRepo.delete(key);
  }

  getAll(): Record<string, string> {
    return this.configRepo.getAll();
  }

  getConfig(): Omit<Config, 'serverPort' | 'serverHost'> {
    const timeoutConfig = getAgentTimeoutConfig(load());
    return {
      agentTimeout: this.configRepo.getNumber(ConfigService.KEYS.AGENT_TIMEOUT, 1800000),
      maxConcurrentAgents: this.configRepo.getNumber(ConfigService.KEYS.AGENT_MAX_CONCURRENT, 8),
      pollInterval: this.configRepo.getNumber(ConfigService.KEYS.POLL_INTERVAL, 30000),
      taskTimeout: timeoutConfig.taskTimeout,
      stageTimeout: timeoutConfig.stageTimeout,
      maxGracePeriods: timeoutConfig.maxGracePeriods,
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

      case ConfigService.KEYS.AGENT_TASK_TIMEOUT:
        const taskTimeout = parseInt(value, 10);
        if (isNaN(taskTimeout) || taskTimeout < 60 || taskTimeout > 7200) {
          return { valid: false, error: 'taskTimeout must be between 60 and 7200 seconds' };
        }
        break;

      case ConfigService.KEYS.AGENT_STAGE_TIMEOUT:
        const stageTimeout = parseInt(value, 10);
        if (isNaN(stageTimeout) || stageTimeout < 300 || stageTimeout > 86400) {
          return { valid: false, error: 'stageTimeout must be between 300 and 86400 seconds' };
        }
        break;

      case ConfigService.KEYS.AGENT_MAX_GRACE_PERIODS:
        const maxGracePeriods = parseInt(value, 10);
        if (isNaN(maxGracePeriods) || maxGracePeriods < 0 || maxGracePeriods > 10) {
          return { valid: false, error: 'maxGracePeriods must be between 0 and 10' };
        }
        break;
    }

    return { valid: true };
  }
}
