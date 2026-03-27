import { createAnthropic } from '@ai-sdk/anthropic';
import { createOpenAI } from '@ai-sdk/openai';
import type { LanguageModelV3 } from '@ai-sdk/provider';

const PROVIDER_ENV: Record<string, string[]> = {
  anthropic: ['ANTHROPIC_API_KEY'],
  openai: ['OPENAI_API_KEY'],
};

const DEFAULT_MODEL = 'anthropic/claude-sonnet-4-20250514';

export interface LlmProviderOptions {
  baseURL?: string;
  apiKey?: string;
}

export interface LlmConfig {
  model?: string;
  provider?: Record<string, { options?: LlmProviderOptions }>;
}

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

  const envVars = PROVIDER_ENV[providerID];
  if (!envVars) {
    throw new Error(
      `Unsupported provider: "${providerID}". Supported providers: ${Object.keys(PROVIDER_ENV).join(', ')}.`
    );
  }

  const apiKey = envVars.map((e) => process.env[e]).find(Boolean);
  if (!apiKey) {
    throw new Error(
      `API key not found for provider "${providerID}". Set one of: ${envVars.join(', ')} environment variables.`
    );
  }

  const options: Record<string, string> = { apiKey };
  const providerOptions = config?.provider?.[providerID]?.options;
  if (providerOptions) {
    if (providerOptions.baseURL) {
      options.baseURL = providerOptions.baseURL;
    }
  }

  switch (providerID) {
    case 'anthropic':
      return createAnthropic(options)(modelID);
    case 'openai':
      return createOpenAI(options)(modelID);
    default:
      throw new Error(`Unsupported provider: "${providerID}".`);
  }
}
