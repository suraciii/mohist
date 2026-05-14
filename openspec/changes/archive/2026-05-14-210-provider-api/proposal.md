## Why

The AI settings page is slow because `GET /api/providers` rebuilds provider metadata and includes thousands of model IDs that the page does not consume from that endpoint. Provider and model data should be served from memory and split by purpose so settings can load quickly without repeatedly serializing a 177KB payload.

## What Changes

- Add a server-side provider state service that keeps provider list and model-group data in memory and prewarms it during server startup.
- Change `GET /api/providers` to return lightweight provider metadata without the `models` field.
- Change `GET /api/providers/models` to read model groups from the shared in-memory provider state instead of rebuilding them per request.
- Refresh the provider state automatically after provider configuration is created, updated, or deleted.
- Keep provider configuration, model selection, and custom provider save/test/delete behavior functionally unchanged.

## Capabilities

### New Capabilities



### Modified Capabilities

- http-api
- provider-config

## Impact

- `packages/cli/src/api/providers.ts`: provider list and model endpoints will depend on cached provider state; the provider list response contract removes `models`.
- `packages/cli/src/server/index.ts`: server startup will instantiate and prewarm provider state, then pass it into provider routes.
- `packages/cli/src/config/builtin-providers.ts` and `packages/cli/src/config/builtin-models.ts`: provider/model resolution remains the source of truth but should be called during cache refresh rather than every read request.
- `packages/cli/src/services/event-bus.ts`: existing `config:providers:changed` events remain available for current consumers while provider config write handlers refresh provider state after configuration changes.
- `packages/cli/web/src/lib/provider-api.ts` and AI settings components: frontend provider types and consumers must stop expecting model IDs from `GET /api/providers`; selectable models continue to come from `GET /api/providers/models`.
