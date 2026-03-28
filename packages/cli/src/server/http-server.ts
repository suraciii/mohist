import express, { Express, Request, Response, Router } from 'express';
import { Config, ServerState } from '../types';

export class HttpServer {
  private app: Express;
  private server: any;
  private config: Config;
  private state: ServerState;

  constructor(config: Config) {
    this.config = config;
    this.app = express();
    this.state = {
      isRunning: false,
      port: config.serverPort
    };
    this.setupMiddleware();
    this.setupRoutes();
  }

  private setupMiddleware(): void {
    this.app.use(express.json());
    this.app.use(express.urlencoded({ extended: true }));
    
    this.app.use((req: Request, _res: Response, next) => {
      console.log(`${new Date().toISOString()} ${req.method} ${req.path}`);
      next();
    });
  }

  private setupRoutes(): void {
    this.app.get('/api/health', (_req: Request, res: Response) => {
      res.json({ status: 'ok', timestamp: new Date().toISOString() });
    });

    this.app.get('/api/status', (_req: Request, res: Response) => {
      res.json({
        success: true,
        data: this.state
      });
    });
  }

  public addRouter(path: string, router: Router): void {
    this.app.use(path, router);
  }

  public getApp(): Express {
    return this.app;
  }

  public getState(): ServerState {
    return this.state;
  }

  public start(): Promise<void> {
    return new Promise((resolve) => {
      this.server = this.app.listen(this.config.serverPort, () => {
        this.state.isRunning = true;
        this.state.startedAt = new Date().toISOString();
        console.log(`Server listening on port ${this.config.serverPort}`);
        resolve();
      });
    });
  }

  public stop(): Promise<void> {
    return new Promise((resolve, reject) => {
      if (!this.server) {
        resolve();
        return;
      }

      this.server.close((err: Error) => {
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
