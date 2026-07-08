### Requirement: IEventPublisher.PublishAsync converges to appending one event row

`IEventPublisher.PublishAsync` SHALL append exactly one event row to the event store. It SHALL be a write-only operation from the producer's perspective. It SHALL NOT synchronously dispatch the event to any `ICloudEventHandler` implementation. Notification of subscribers SHALL NOT be a responsibility of the publish path.

#### Scenario: Publish appends a single event row

- **WHEN** a producer calls `IEventPublisher.PublishAsync(envelope)`
- **THEN** exactly one event row SHALL be appended to the event store
- **AND** the appended row SHALL carry the envelope's source, type, subject, data, and extensions

#### Scenario: Publish accepts the typed overload and appends one row

- **WHEN** a producer calls the typed `PublishAsync<TData>(data, type, source, subject, extensions)` overload
- **THEN** exactly one event row SHALL be appended to the event store
- **AND** the row SHALL reflect the supplied type, source, subject, data, and extensions

#### Scenario: Publish does not synchronously invoke handlers

- **WHEN** a producer calls `IEventPublisher.PublishAsync`
- **AND** one or more `ICloudEventHandler` implementations are registered whose `[Subscription]` type matches the event
- **THEN** no matching handler's `HandleAsync` SHALL be invoked during the publish call
- **AND** the publish call SHALL return once the event row has been appended

### Requirement: Synchronous fan-out removed from the publish path

The synchronous fan-out previously performed by `InMemoryEventBus` during `PublishAsync` (iterating `Subscription`s and invoking each matching handler on the producer's call stack) SHALL be removed from the publish path. Registered `ICloudEventHandler` implementations SHALL remain registered for the future dispatcher but SHALL NOT be triggered by publish. The `DispatchAsync` synchronous-dispatch loop SHALL NOT run as a consequence of `PublishAsync`.

#### Scenario: Matching subscriptions are not dispatched during publish

- **WHEN** `PublishAsync` is called on the event publisher
- **AND** one or more `Subscription`s whose type matches the event are registered
- **THEN** no matching `Subscription` dispatch delegate SHALL be invoked from within the publish call

#### Scenario: Handler exception cannot affect the publish path

- **WHEN** a registered handler would throw if dispatched
- **AND** a producer calls `PublishAsync` for a matching event type
- **THEN** the publish SHALL NOT invoke the handler
- **AND** no handler exception SHALL propagate into or be logged from the publish path
