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
    stageTimeout: z.number().optional(),
    taskTimeout: z.number().optional(),
    maxConcurrent: z.number().optional(),
    maxGracePeriods: z.number().optional(),
    pollInterval: z.number().optional(),
  }).superRefine((agent, ctx) => {
    if (agent.taskTimeout !== undefined) {
      if (agent.taskTimeout < 60) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['taskTimeout'],
          message: 'taskTimeout must be at least 60 seconds',
        });
      }
      if (agent.taskTimeout > 7200) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['taskTimeout'],
          message: 'taskTimeout must be at most 7200 seconds (2 hours)',
        });
      }
    }
    if (agent.stageTimeout !== undefined) {
      if (agent.stageTimeout < 300) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['stageTimeout'],
          message: 'stageTimeout must be at least 300 seconds (5 minutes)',
        });
      }
      if (agent.stageTimeout > 86400) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['stageTimeout'],
          message: 'stageTimeout must be at most 86400 seconds (24 hours)',
        });
      }
    }
    if (agent.maxGracePeriods !== undefined) {
      if (agent.maxGracePeriods < 0) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['maxGracePeriods'],
          message: 'maxGracePeriods must be at least 0',
        });
      }
      if (agent.maxGracePeriods > 10) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          path: ['maxGracePeriods'],
          message: 'maxGracePeriods must be at most 10',
        });
      }
    }
  }).optional(),
  log: z.object({
    level: z.enum(['DEBUG', 'INFO', 'WARN', 'ERROR']).optional(),
  }).strip().optional(),
  opencode: z.object({
    binPath: z.string().optional(),
    model: z.string().optional(),
    stageModels: z.record(z.string(), z.string()).optional(),
  }).strip().optional(),
}).strip();

export type ConfigInfo = z.infer<typeof ConfigInfoSchema>;
