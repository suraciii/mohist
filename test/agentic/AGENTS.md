# Agentic Testing Context

## Structure

```
test/agentic/
├── shared/              # Shared infrastructure
│   ├── Containerfile    # Base container
│   └── entrypoint.sh    # Starts Mohist.Server
└── verify-<feature>/
    ├── TESTPLAN.md      # Agent-readable test plan
    └── scripts/         # Helper scripts (one script, one job)
        └── <name>.sh
```

## Container

- User: `motest`
- Workspace: `/app/workspace/`
- Data: `/home/motest/.mohist/`
- mohist source: `/opt/mohist-src` (built)
- Server: `localhost:3456` (started by entrypoint)
