# Proposal: Enforce Agent Task Scope at Launch

The task profile currently stores `purpose` and declared `permissions`, but
those fields do not participate in admission. A caller can therefore create a
Job and Session for an Agent that has no purpose or has not declared access to
the Issue, Epic, repository, or workspace supplied by the launch.

This change adds a deterministic launch admission gate. It runs before the
first Session or Job write on every shared AgentLauncher path and freezes the
accepted task scope into the launch execution snapshot. A scoped launch is
rejected when the Agent has no purpose or its declaration does not cover the
read scope implied by the launch context.

The gate does not infer whether arbitrary natural-language work is compatible
with a free-text purpose, and it does not claim to enforce tool writes. Those
facts require an explicit requested-capability contract and a Runner/tool
owner. The Workflow `mohist/agent` translator bypasses `IAgentLauncher`; it is
therefore a separate integration boundary and remains design-only in this
slice unless its owner accepts the same snapshot contract.
