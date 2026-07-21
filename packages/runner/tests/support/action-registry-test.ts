import type { ActionContext, ActionResult, JsonObject } from "../../src/core/types.js"
import { defineAction } from "../../src/actions/define-action.js"
import type { ActionDefinition, ActionManifest } from "../../src/actions/manifest.js"
import type { ActionHost } from "../../src/actions/host.js"
import { ActionRegistry } from "../../src/actions/registry.js"

export interface TestActionOptions {
  inputs?: ActionManifest["inputs"]
  outputs?: ActionManifest["outputs"]
  errors?: ActionManifest["errors"]
  description?: string
  capabilities?: ActionManifest["capabilities"]
}

const NAMESPACED_TEST_NAME = /^[a-z0-9]+(?:-[a-z0-9]+)*\/[a-z0-9]+(?:-[a-z0-9]+)*$/

export function makeHost(overrides: Partial<ActionHost> = {}): ActionHost {
  return {
    workDir: "/tmp/test-workdir",
    signal: new AbortController().signal,
    log: null,
    exec: async () => ({ exitCode: 0, stdout: "", stderr: "" }),
    ...overrides,
  }
}

function contextFromHost(inputs: JsonObject, host: ActionHost): ActionContext {
  return {
    workflowRunId: "",
    workId: "",
    workType: "task",
    workDir: host.workDir,
    signal: host.signal,
    variables: {},
    with: inputs,
    log: host.log,
    writeVars: async () => {},
  }
}

function adaptHandler(handler: (context: ActionContext) => Promise<ActionResult>): (inputs: JsonObject, host: ActionHost) => Promise<ActionResult> {
  return (inputs, host) => handler(contextFromHost(inputs, host))
}

export function defineTestAction(
  name: string,
  handler: ((context: ActionContext) => Promise<ActionResult>) | ((inputs: JsonObject, host: ActionHost) => Promise<ActionResult>),
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
    capabilities: options.capabilities,
  }
  const needsAdapt = typeof handler === "function" && handler.length <= 1
  const adapted = needsAdapt ? adaptHandler(handler as (context: ActionContext) => Promise<ActionResult>) : handler
  return defineAction({ manifest, run: adapted as unknown as ActionDefinition["run"] })
}

type TestHandler =
  | ((context: ActionContext) => Promise<ActionResult>)
  | ((inputs: JsonObject, host: ActionHost) => Promise<ActionResult>)
  | { run: ((context: ActionContext) => Promise<ActionResult>) | ((inputs: JsonObject, host: ActionHost) => Promise<ActionResult>); inputs?: ActionManifest["inputs"]; outputs?: ActionManifest["outputs"]; errors?: ActionManifest["errors"]; description?: string; capabilities?: ActionManifest["capabilities"] }

export function defineTestActions(actions: Record<string, TestHandler>): ActionRegistry {
  const definitions = Object.entries(actions).map(([name, value]) => {
    if (typeof value === "function") {
      if (value.length <= 1) {
        return defineTestAction(name, adaptHandler(value as (context: ActionContext) => Promise<ActionResult>))
      }
      return defineTestAction(name, value)
    }
    if (value.run.length <= 1) {
      return defineTestAction(name, adaptHandler(value.run as (context: ActionContext) => Promise<ActionResult>), {
        inputs: value.inputs,
        outputs: value.outputs,
        errors: value.errors,
        description: value.description,
        capabilities: value.capabilities,
      })
    }
    return defineTestAction(name, value.run, {
      inputs: value.inputs,
      outputs: value.outputs,
      errors: value.errors,
      description: value.description,
      capabilities: value.capabilities,
    })
  })
  return new ActionRegistry(definitions)
}

export interface TestActionDefinition {
  run: (inputs: JsonObject, host: ActionHost) => Promise<ActionResult>
  inputs?: ActionManifest["inputs"]
  outputs?: ActionManifest["outputs"]
  errors?: ActionManifest["errors"]
  description?: string
  capabilities?: ActionManifest["capabilities"]
}

export { ActionRegistry }
