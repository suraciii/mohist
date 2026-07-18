/**
 * OpenCode model catalog loader.
 *
 * Loads provider and model data via the read-only v2 list APIs
 * (`client.v2.provider.list()`, `client.v2.model.list()`). The shape
 * is normalized to Mohist-owned `RuntimeModelCatalog` so the
 * generated SDK DTOs never escape this module.
 *
 * Per spec `specs/opencode-model-catalog/spec.md` the catalog is a
 * configuration hint; the runtime does NOT pre-validate model
 * legality beyond the `provider/modelID` shape.
 */

import type { OpencodeClient } from "@opencode-ai/sdk/v2"
import type { RuntimeModelCatalog, RuntimeModelDescriptor } from "./types.js"

export interface CatalogClient {
  list(): Promise<RuntimeModelCatalog>
}

export type CatalogClientFactory = (client: OpencodeClient) => CatalogClient

export function createCatalogClient(client: OpencodeClient): CatalogClient {
  return {
    async list(): Promise<RuntimeModelCatalog> {
      const models = await client.v2.model.list().catch(() => null)
      const modelData = models?.data?.data ?? []
      const descriptors: RuntimeModelDescriptor[] = modelData.map((model) => ({
        providerID: model.providerID,
        modelID: model.id,
        variants: Array.isArray(model.variants) ? model.variants.map((v) => v.id) : [],
      }))
      return { models: descriptors, fetchedAt: Date.now() }
    },
  }
}
