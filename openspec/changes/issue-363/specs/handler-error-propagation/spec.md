### Requirement: Event handlers propagate failures to the durable dispatcher

Every `ICloudEventHandler` / `ICloudEventHandler<TData>` implementation SHALL let non-cancellation exceptions thrown during event processing propagate to the durable event dispatcher. The dispatcher's unified per-handler retry, backoff, and dead-letter path (established by the durable dispatch contract) SHALL be the single place where handler failures are aggregated. A handler SHALL NOT swallow a delivery failure by catching a non-cancellation exception, logging it, and returning a completed task. A handler SHALL NOT detach its side effect from the dispatch call stack (fire-and-forget without awaiting) in order to hide a failure from the dispatcher.

#### Scenario: Handler failure reaches the dispatcher

- **WHEN** a handler's `HandleAsync` throws a non-cancellation exception while processing an event
- **THEN** the exception SHALL propagate to the durable dispatcher
- **AND** the dispatcher SHALL apply its per-handler retry and, on exhaustion, dead-letter the event for that handler
- **AND** the handler SHALL NOT have absorbed the exception via a catch-log-return

#### Scenario: Cancellation is cooperatively rethrown, not swallowed

- **WHEN** a handler's `HandleAsync` observes an `OperationCanceledException` while the cancellation token is cancelled
- **THEN** the handler SHALL rethrow the `OperationCanceledException`
- **AND** the dispatcher SHALL treat cancellation as cooperative cancellation, not a handler delivery failure

### Requirement: AgentSubscriptionDispatchHandler does not swallow dispatch failures

`AgentSubscriptionDispatchHandler.HandleAsync` SHALL NOT wrap its dispatch body in a try-catch that logs-and-returns on non-cancellation exceptions. Subscription dispatch failures (e.g. scope resolution, store query, arbitration, agent launch) SHALL propagate to the durable dispatcher. The handler SHALL still perform its envelope-level skips (no project id on the envelope, no active matched subscription, empty rendered prompt) by returning a completed task without throwing — those are valid no-op outcomes, not failures.

#### Scenario: Subscription launch failure propagates

- **WHEN** `IAgentLauncher.LaunchAsync` throws during subscription dispatch
- **THEN** the exception SHALL propagate out of `AgentSubscriptionDispatchHandler.HandleAsync`
- **AND** the dispatcher SHALL retry or dead-letter the event for this handler
- **AND** the handler SHALL NOT log-and-return a completed task that hides the failure

#### Scenario: No-project-id envelope is still a no-op, not a failure

- **WHEN** an event envelope carries no `projectid` extension
- **THEN** `AgentSubscriptionDispatchHandler.HandleAsync` SHALL return a completed task without throwing
- **AND** the dispatcher SHALL treat the event as delivered for this handler

### Requirement: HermesIssueNotificationHandler does not swallow notification failures

`HermesIssueNotificationHandler.HandleAsync` SHALL NOT wrap its body in a try-catch that logs-and-returns on non-cancellation exceptions. Notification setup failures (options resolution, notification-type resolution, dispatch enqueue) SHALL propagate to the durable dispatcher. Handler-level disabled-notifications and unconfigured-webhook outcomes SHALL remain no-ops (return a completed task without throwing) — those are valid skips, not failures.

#### Scenario: Notification dispatch setup failure propagates

- **WHEN** `HermesIssueNotificationHandler.HandleAsync` throws a non-cancellation exception during setup or dispatch
- **THEN** the exception SHALL propagate to the durable dispatcher
- **AND** the handler SHALL NOT catch-log-return a completed task that hides the failure

#### Scenario: Disabled notification type is still a no-op, not a failure

- **WHEN** a notification type is disabled in `HermesNotificationOptions` or the webhook is not configured
- **THEN** `HermesIssueNotificationHandler.HandleAsync` SHALL return a completed task without throwing
- **AND** the dispatcher SHALL treat the event as delivered for this handler

### Requirement: RunnerWorkflowTerminalStatusHandler awaits the router and propagates failures

`RunnerWorkflowTerminalStatusHandler.HandleAsync` SHALL await the runner workflow status router call synchronously on the dispatch stack. The handler SHALL NOT detach the router call from the dispatch stack. Delivery failures from the router SHALL propagate to the durable dispatcher for unified retry and dead-lettering. The handler SHALL NOT contain stale prose referencing a detach model or an in-stack delivery assumption.

#### Scenario: Router delivery failure propagates

- **WHEN** `IRunnerWorkflowStatusRouter.RouteAsync` throws while routing a terminal workflow status
- **THEN** the exception SHALL propagate to the durable dispatcher
- **AND** the handler SHALL NOT have detached the router call or swallowed the exception

#### Scenario: Handler documentation reflects the awaited-delivery model

- **WHEN** the `RunnerWorkflowTerminalStatusHandler` XML doc comments and prose are inspected
- **THEN** they SHALL describe the router call as awaited with failures propagating to the durable dispatcher
- **AND** they SHALL NOT reference a detach decision, an in-stack delivery assumption, or a synchronous-callback deadlock workaround
