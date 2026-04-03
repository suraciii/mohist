import { Hono } from 'hono';
import { serve } from '@hono/node-server';
import { Config, ServerState } from '../types';

export class HttpServer {
  private app: Hono;
  private server: ReturnType<typeof serve> | null;
  private config: Config;
  private state: ServerState;

  constructor(config: Config) {
    this.config = config;
    this.app = new Hono();
    this.server = null;
    this.state = {
      isRunning: false,
      port: config.serverPort
    };
    this.setupMiddleware();
    this.setupRoutes();
  }

  private setupMiddleware(): void {
    this.app.use('*', async (c, next) => {
      console.log(`${new Date().toISOString()} ${c.req.method} ${c.req.path}`);
      await next();
    });
  }

  private setupRoutes(): void {
    this.app.get('/api/health', (c) => {
      return c.json({ status: 'ok', timestamp: new Date().toISOString() });
    });

    this.app.get('/api/status', (c) => {
      return c.json({
        success: true,
        data: this.state
      });
    });
  }

  public addRouter(path: string, router: Hono): void {
    this.app.route(path, router);
  }

  public getApp(): Hono {
    return this.app;
  }

  public getState(): ServerState {
    return this.state;
  }

  public start(): Promise<void> {
    return new Promise((resolve) => {
      this.server = serve(
        { fetch: this.app.fetch, port: this.config.serverPort },
        (info) => {
          this.state.isRunning = true;
          this.state.startedAt = new Date().toISOString();
          console.log(`Server listening on port ${info.port}`);
          resolve();
        }
      );
    });
  }

  public stop(): Promise<void> {
    return new Promise((resolve, reject) => {
      if (!this.server) {
        resolve();
        return;
      }

      this.server.close((err?: Error) => {
        if (err) {
          reject(err);
        } else {
          this.state.isRunning = false;
          console.log('Server stopped');
          resolve();
        }
      });
    });
  }
}
