# Agentic Testing

Container-isolated end-to-end tests for Mohist.

## Structure

```text literal
test/agentic/
├── README.md
├── AGENTS.md
├── shared/                           # Shared infrastructure
│   ├── Containerfile                 # Base container (.NET SDK + Web UI build)
│   └── entrypoint.sh                 # Server startup
└── verify-<feature>/                 # Per-test
    ├── TESTPLAN.md                   # Agent-readable test plan (natural language + @ references)
    └── scripts/                      # Helper scripts (each does ONE thing)
        └── <name>.sh
```

## TESTPLAN.md Convention

`TESTPLAN.md` is the test plan that an Agent reads and executes.

- Describe each phase's steps and expected results in natural language.
- The Agent runs simple commands itself (`curl`, API calls, `which`, and so on).
- Call a helper script with `@scripts/<name>.sh` only for complex operations
  that must be deterministic.

Example:

```markdown
## Phase 5: Data Persistence

1. Record the current Issue count.
2. @scripts/restart-server.sh
3. Verify that the data is intact.
```

## scripts/ Convention

Each script does exactly one thing, and its name states what it does:

```bash
scripts/restart-server.sh   # Stops Mohist.Server, restarts it, and waits for the health check to pass.
```

Scripts are idempotent, exit with a clear code (0 = success, 1 = failure), and
print concise status output.

## Container Environment

- User: `motest`
- Workspace: `/app/workspace/`
- Data: `/home/motest/.mohist/`
- Mohist source: `/opt/mohist-src` (built)
- Server: `localhost:3456`, started by the entrypoint

## Creating a New Test

Create `test/agentic/verify-<feature>/` by hand: write `TESTPLAN.md` and any
helper scripts under `scripts/`. Tests target the ASP.NET Core Server, the
TypeScript Runner, and the HTTP API directly.
