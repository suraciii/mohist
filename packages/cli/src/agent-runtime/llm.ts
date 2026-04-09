import { createAnthropic } from '@ai-sdk/anthropic';
import { createOpenAI } from '@ai-sdk/openai';
import type { LanguageModelV3 } from '@ai-sdk/provider';
import type { ConfigInfo } from '../config/config-schema';
import { getProviderConfig, getConfigPath } from '../config/config-loader';

export type LlmConfig = ConfigInfo;

export class LlmError extends Error {
  code: string;
  constructor(code: string, message: string) {
    super(message);
    this.code = code;
    this.name = 'LlmError';
  }
}

const DEFAULT_MODEL = 'anthropic/claude-sonnet-4-20250514';

export function resolveModel(config?: LlmConfig): LanguageModelV3 {
  const modelStr = config?.model ?? DEFAULT_MODEL;
  const slashIndex = modelStr.indexOf('/');
  if (slashIndex === -1) {
    throw new Error(
      `Invalid model format: "${modelStr}". Expected "provider/model-id" format.`
    );
  }

  const providerID = modelStr.slice(0, slashIndex);
  const modelID = modelStr.slice(slashIndex + 1);

  if (!providerID || !modelID) {
    throw new Error(
      `Invalid model format: "${modelStr}". Expected "provider/model-id" format.`
    );
  }

  const resolved = getProviderConfig(config ?? {}, providerID);

  if (!resolved.apiKey) {
    const envHint = resolved.envVars.length > 0
      ? ` or set ${resolved.envVars.join(', ')} environment variable`
      : '';
    throw new LlmError(
      'LLM_NOT_CONFIGURED',
      `API key not found for provider "${providerID}". Set provider.${providerID}.apiKey in ${getConfigPath()}${envHint}.`
    );
  }

  const sdkOpts: Record<string, string> = { apiKey: resolved.apiKey };
  if (resolved.baseURL) {
    sdkOpts.baseURL = resolved.baseURL;
  }

  switch (resolved.sdk) {
    case 'anthropic':
      return createAnthropic(sdkOpts)(modelID);
    case 'openai':
      return createOpenAI(sdkOpts)(modelID);
    case 'openai-compatible':
      return createOpenAI(sdkOpts)(modelID);
    default:
      return createOpenAI(sdkOpts)(modelID);
  }
}
