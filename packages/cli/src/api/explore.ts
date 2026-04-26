import { Hono } from 'hono';
import { streamSSE } from 'hono/streaming';
import type { SSEStreamingApi } from 'hono/streaming';
import { ApiResponse, ExploreSession, ToolCallRecord } from '../types';
import { ExploreService, IssueService, ExploreAcpService } from '../services';
import { runExploreAgent } from '../agents/explore-agent';
import { ExploreSessionRepo } from '../db/explore-session-repo';
import { ProjectService } from '../services/project-service';
import type { LlmConfig } from '../agent-runtime';
import { LlmError } from '../agent-runtime/llm';
import type { EventBus } from '../services/event-bus';
import { clearConfigCache, load } from '../config/config-loader';
import { getModelById } from '../config/builtin-models';
import { Log } from '../util/log';

const log = Log.create({ service: 'explore' });

export type ExploreAcpFactory = (projectPath: string) => ExploreAcpService;

export function createExploreRoutes(
  exploreService: ExploreService,
  issueService: IssueService,
  projectService: ProjectService,
  exploreSessionRepo: ExploreSessionRepo,
  eventBus: EventBus,
  exploreAcpFactory?: ExploreAcpFactory,
): Hono {
  const app = new Hono();

  const getCurrentProjectId = (): string | null => {
    return projectService.getCurrentId();
  };

  app.post('/', async (c) => {
    try {
      const body = await c.req.json();
      const projectId = body.projectId || getCurrentProjectId();
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>',
        };
        return c.json(response, 400);
      }

      const title = body.title || 'New Exploration';
      const issueId: string | undefined = body.issueId;
      let issueNumber: number | undefined;

      if (issueId) {
        const issue = issueService.getById(issueId);
        if (!issue) {
          const response: ApiResponse = {
            success: false,
            error: `Issue not found: ${issueId}`,
          };
          return c.json(response, 404);
        }

        const existingSession = exploreService.findSessionByIssueId(issueId);
        if (existingSession) {
          const response: ApiResponse = {
            success: false,
            error: 'Issue is already linked to another explore session',
          };
          return c.json(response, 409);
        }

        issueNumber = issue.number;
      }

      const session = exploreService.createSession({ projectId, title, issueId });
      const response: ApiResponse<ExploreSession> = {
        success: true,
        data: { ...session, issueNumber },
      };
      return c.json(response, 201);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Failed to create session',
      };
      return c.json(response, 500);
    }
  });

  app.get('/', async (c) => {
    try {
      const projectId = c.req.query('projectId') || getCurrentProjectId();
      if (!projectId) {
        const response: ApiResponse = {
          success: false,
          error: 'No active project. Use: mo project use <name>',
        };
        return c.json(response, 400);
      }

      const status = c.req.query('status');
      const sessions = exploreService.listSessions(projectId, status);
      const response: ApiResponse<ExploreSession[]> = {
        success: true,
        data: sessions,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Failed to list sessions',
      };
      return c.json(response, 500);
    }
  });

  app.get('/:id', async (c) => {
    try {
      const id = c.req.param('id');
      const result = exploreService.getSession(id);
      if (!result) {
        const response: ApiResponse = {
          success: false,
          error: 'Session not found',
        };
        return c.json(response, 404);
      }

      const response: ApiResponse<typeof result> = {
        success: true,
        data: result,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Failed to get session',
      };
      return c.json(response, 500);
    }
  });

  app.delete('/:id', async (c) => {
    try {
      const id = c.req.param('id');
      const session = exploreService.getSession(id);
      if (!session) {
        const response: ApiResponse = {
          success: false,
          error: 'Session not found',
        };
        return c.json(response, 404);
      }

      exploreService.deleteSession(id);
      const response: ApiResponse = {
        success: true,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Failed to delete session',
      };
      return c.json(response, 500);
    }
  });

  app.post('/:id/model', async (c) => {
    try {
      const id = c.req.param('id');
      const body = await c.req.json();
      const { model, variant } = body;

      if (!model || typeof model !== 'string') {
        const response: ApiResponse = {
          success: false,
          error: 'model is required',
        };
        return c.json(response, 400);
      }

      if (!model.includes('/')) {
        const response: ApiResponse = {
          success: false,
          error: `Invalid model format: expected provider/model-id`,
        };
        return c.json(response, 400);
      }

      const modelMetadata = await getModelById(model);
      if (!modelMetadata) {
        const response: ApiResponse = {
          success: false,
          error: `Invalid model: ${model}`,
        };
        return c.json(response, 400);
      }

      const session = exploreSessionRepo.findById(id);
      if (!session) {
        const response: ApiResponse = {
          success: false,
          error: 'Session not found',
        };
        return c.json(response, 404);
      }

      const updatedSession = exploreSessionRepo.updateModel(id, model, variant ?? null);
      const response: ApiResponse<ExploreSession> = {
        success: true,
        data: updatedSession!,
      };
      return c.json(response);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Failed to update session model',
      };
      return c.json(response, 500);
    }
  });

  app.post('/:id/crystallize', async (c) => {
    try {
      if (!exploreAcpFactory) {
        const response: ApiResponse = {
          success: false,
          error: 'ACP explore not configured',
        };
        return c.json(response, 503);
      }

      const sessionId = c.req.param('id');
      const sessionData = exploreService.getSession(sessionId);
      if (!sessionData) {
        const response: ApiResponse = {
          success: false,
          error: 'Session not found',
        };
        return c.json(response, 404);
      }

      const session = sessionData.session;
      const project = projectService.getById(session.projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found',
        };
        return c.json(response, 400);
      }

      const issueNumber = session.issueId
        ? (() => {
            const issue = issueService.getById(session.issueId);
            return issue ? issue.number : null;
          })()
        : null;

      if (!issueNumber) {
        const body = await c.req.json().catch(() => ({}));
        const title = body.title || session.title || 'New Issue';

        const acpService = exploreAcpFactory(project.path);
        const result = await acpService.run(title, session.projectId);

        if (result.success && result.issueNumber) {
          exploreService.crystallize(sessionId, String(result.issueNumber));
        }

        const response: ApiResponse<typeof result> = {
          success: result.success,
          data: result,
          error: result.error,
        };
        return c.json(response, result.success ? 201 : 500);
      }

      const issue = issueService.getByNumber(session.projectId, issueNumber);
      if (!issue) {
        const response: ApiResponse = {
          success: false,
          error: `Issue #${issueNumber} not found`,
        };
        return c.json(response, 404);
      }

      const acpService = exploreAcpFactory(project.path);
      const result = await acpService.runOnIssue(issue);

      const response: ApiResponse<typeof result> = {
        success: result.success,
        data: result,
        error: result.error,
      };
      return c.json(response, result.success ? 200 : 500);
    } catch (error) {
      const response: ApiResponse = {
        success: false,
        error: error instanceof Error ? error.message : 'Failed to crystallize',
      };
      return c.json(response, 500);
    }
  });

  app.post('/:id/messages', async (c) => {
    try {
      const sessionId = c.req.param('id');
      const sessionData = exploreService.getSession(sessionId);
      if (!sessionData) {
        const response: ApiResponse = {
          success: false,
          error: 'Session not found',
        };
        return c.json(response, 404);
      }

      const body = await c.req.json();
      const userContent = body.content;
      if (!userContent || typeof userContent !== 'string') {
        const response: ApiResponse = {
          success: false,
          error: 'content is required',
        };
        return c.json(response, 400);
      }

      const session = sessionData.session;
      const project = projectService.getById(session.projectId);
      if (!project) {
        const response: ApiResponse = {
          success: false,
          error: 'Project not found',
        };
        return c.json(response, 400);
      }

      const existingMessages = exploreService.getMessages(sessionId);
      const historyMessages = [
        ...existingMessages.map((m) => ({
          role: m.role,
          content: m.content,
        })),
        { role: 'user' as const, content: userContent },
      ];

      clearConfigCache();
      const globalConfig = load();
      const mergedConfig: LlmConfig = {
        ...globalConfig,
        model: session.model ?? globalConfig.model,
      };

      let agentIssueId: string | undefined;
      let agentIssueStage: string | undefined;
      if (session.issueId) {
        const linkedIssue = issueService.getById(session.issueId);
        if (linkedIssue) {
          agentIssueId = linkedIssue.id;
          agentIssueStage = linkedIssue.stage;
        }
      }

      const agentContext = {
        projectPath: project.path,
        sessionId,
        projectId: session.projectId,
        llmConfig: mergedConfig,
        issueService,
        exploreSessionRepo,
        eventBus,
        issueId: agentIssueId,
        issueStage: agentIssueStage,
      };

      exploreService.addMessage(sessionId, 'user', userContent);

      const result = await runExploreAgent(agentContext, historyMessages);

      return streamSSE(c, async (stream: SSEStreamingApi) => {
        let assistantContent = '';
        const toolCallRecords: ToolCallRecord[] = [];
        let createdIssueId: string | null = null;
        try {

          for await (const part of result.fullStream) {
            if (part.type === 'text-delta') {
              assistantContent += part.text;
              await stream.writeSSE({
                data: JSON.stringify({ type: 'chunk', content: part.text }),
              });
            } else if (part.type === 'tool-call') {
              // tool-call has args as the tool input
            } else if (part.type === 'tool-result') {
              const record: ToolCallRecord = {
                name: part.toolName,
                args: part.input as Record<string, unknown>,
                result: part.output,
              };
              toolCallRecords.push(record);

              if (part.toolName === 'create_issue') {
                const outputStr = String(part.output ?? '');
                const match = outputStr.match(/Issue #(\d+)/);
                if (match) {
                  createdIssueId = match[1];
                }
              }

              await stream.writeSSE({
                data: JSON.stringify({
                  type: 'tool_call',
                  tool: part.toolName,
                  args: part.input,
                  result: part.output,
                }),
              });
            }
          }

          const finalText = await result.text;

          if (!createdIssueId) {
            const updatedSession = exploreSessionRepo.findById(sessionId);
            if (updatedSession?.issueId) {
              const issue = issueService.getById(updatedSession.issueId);
              createdIssueId = issue ? String(issue.number) : null;
            }
          }

          exploreService.addMessage(
            sessionId,
            'assistant',
            finalText,
            toolCallRecords.length > 0 ? toolCallRecords : undefined,
          );

          await stream.writeSSE({
            data: JSON.stringify({ type: 'done', issueId: createdIssueId }),
          });
        } catch (error) {
          if (assistantContent) {
            try {
              exploreService.addMessage(sessionId, 'assistant', assistantContent);
            } catch (saveError) {
              log.error('Failed to save partial message', {
                error: saveError instanceof Error ? saveError.message : saveError,
              });
            }
          }
          log.error('Stream error', { error: error instanceof Error ? error.message : error });
          await stream.writeSSE({
            data: JSON.stringify({
              type: 'done',
              issueId: null,
              error: error instanceof Error ? error.message : 'Stream error',
            }),
          });
        }
      });
    } catch (error) {
      const response: ApiResponse & { code?: string } = {
        success: false,
        error: error instanceof Error ? error.message : 'Failed to send message',
      };
      if (error instanceof LlmError) {
        response.code = error.code;
      }
      return c.json(response, 500);
    }
  });

  return app;
}
