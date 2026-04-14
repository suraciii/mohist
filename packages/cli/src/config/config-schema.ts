import { z } from 'zod';

export type SdkType = 'anthropic' | 'openai' | 'openai-compatible';

export const ProviderConfigSchema = z.object({
  name: z.string().optional(),
  apiKey: z.string().optional(),
  baseURL: z.string().optional(),
  sdk: z.enum(['anthropic', 'openai', 'openai-compatible']).optional(),
  models: z.array(z.string()).optional(),
}).strip();

export type ProviderConfig = z.infer<typeof ProviderConfigSchema>;

export const ConfigInfoSchema = z.object({
  $schema: z.string().optional(),
  _version: z.number().optional(),
  model: z.string().optional(),
  provider: z.record(z.string(), ProviderConfigSchema).optional(),
  server: z.object({
    port: z.number().optional(),
    host: z.string().optional(),
  }).strip().optional(),
  agent: z.object({
    timeout: z.number().optional(),
    maxConcurrent: z.number().optional(),
  }).strip().optional(),
  log: z.object({
    level: z.enum(['DEBUG', 'INFO', 'WARN', 'ERROR']).optional(),
  }).strip().optional(),
}).strip();

export type ConfigInfo = z.infer<typeof ConfigInfoSchema>;
