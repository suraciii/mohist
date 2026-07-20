import type { ActionContext, ActionResult } from "../../src/core/types.js"
import { defineAction } from "../../src/actions/define-action.js"
import type { ActionDefinition, ActionManifest } from "../../src/actions/manifest.js"
import { ActionRegistry } from "../../src/actions/registry.js"

export interface TestActionOptions {
  inputs?: ActionManifest["inputs"]
  outputs?: ActionManifest["outputs"]
  errors?: ActionManifest["errors"]
  description?: string
}

const NAMESPACED_TEST_NAME = /^[a-z0-9]+(?:-[a-z0-9]+)*\/[a-z0-9]+(?:-[a-z0-9]+)*$/

/**
 * Build an {@link ActionDefinition} for a test stub. Test stubs typically
 * do not need a rich manifest contract — they exercise dispatch,
 * recovery, and branch-stability paths rather than input validation.
 *
 * The helper still flows through {@link defineAction} so the registry's
 * construction invariants (canonical name, non-empty defaults where
 * declared, reserved-error rejection) stay authoritative; it only
 * supplies an empty contract by default so the stub accepts any
 * `with: {}`-shaped payload. Tests that exercise validation or output
 * projection must declare the relevant inputs/outputs/errors.
 */
export function defineTestAction(
  name: string,
  handler: (context: ActionContext) => Promise<ActionResult>,
  options: TestActionOptions = {},
): ActionDefinition {
  if (!NAMESPACED_TEST_NAME.test(name)) {
    throw new Error(`Test Action name '${name}' must match lowercase <namespace>/<action>`)
  }
  const manifest: ActionManifest = {
    name,
    description: options.description,
    inputs: options.inputs ?? {},
    outputs: options.outputs ?? [],
    errors: options.errors ?? [],
  }
  return defineAction({ manifest, run: handler })
}

/**
 * Convenience: build an {@link ActionRegistry} containing one or more
 * test Actions keyed by their canonical name. Mirrors the shape of
 * production registry construction without the bare-handler bypass
 * API.
 */
export function defineTestActions(actions: Record<string, TestActionDefinition | ((context: ActionContext) => Promise<ActionResult>)>): ActionRegistry {
  const definitions = Object.entries(actions).map(([name, value]) => {
    if (typeof value === "function") {
      return defineTestAction(name, value)
    }
    return defineTestAction(name, value.run, {
      inputs: value.inputs,
      outputs: value.outputs,
      errors: value.errors,
      description: value.description,
    })
  })
  return new ActionRegistry(definitions)
}

export interface TestActionDefinition {
  run: (context: ActionContext) => Promise<ActionResult>
  inputs?: ActionManifest["inputs"]
  outputs?: ActionManifest["outputs"]
  errors?: ActionManifest["errors"]
  description?: string
}

export { ActionRegistry }
