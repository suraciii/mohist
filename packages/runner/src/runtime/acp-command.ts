export function acpCommand() {
  return process.env.MOHIST_AGENT_COMMAND ?? "opencode"
}

export function acpArgs() {
  return process.env.MOHIST_AGENT_ARGS ? JSON.parse(process.env.MOHIST_AGENT_ARGS) as string[] : ["acp", "--pure"]
}
