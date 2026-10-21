# 3. The Greenhaus API service

Status: accepted

## Context
The product rules (tiers, zones, requirements, safety limits) were first written as a mock so the web app could be built against the contract. The real service has to behave the same way, and it has to stay a thin layer over the generic platforms: Pulse knows devices and readings, Orderflow knows orders, and neither knows what a pot is.

## Decision
- `Greenhaus.Domain` holds the rules as pure code with no framework: tier limits, requirement suggestion, reading evaluation and the actuator safety checks. The mock and the service are tested against the same expectations.
- `Greenhaus.Api` is a minimal-API service on PostgreSQL with EF Core, in its own `greenhaus` schema, with migrations applied on startup. It stores pots, zones, plants, requirements, linked devices, channels, actuators, the audit log and care tasks.
- Everything a caller can see is scoped to the token's subject. A pot that belongs to someone else answers 404, not 403.
- The tier comes from billing. The service keeps the last entitlement billing announced and uses the configured free tier until it has heard anything.
- Authentication is Keycloak JWT in normal use. A development mode trusts an `X-User-Id` header so the service runs without an identity provider on a laptop. It logs a warning at startup and is never the default.
- Pulse is reached through one interface, `IPulseClient`: claim, device description, alert rules, calibration, run and kill-switch commands and the latest reading. The HTTP client uses a cached client-credentials token. Tests use an in-memory fake that enforces the claim rules, and a separate test pins the HTTP wire format (paths, field names, error codes).
- A device Pulse cannot describe, or a sensor with no recent reading, is shown as unknown or offline, never as healthy. Rule sync and kill-switch propagation are best effort and logged: the pot's own data is the truth and the next change repeats the sync.
- Safety sensors Pulse reports (tank level, overflow, flow) are not Greenhaus zone metrics and are left out of zones and rules.
- Events are written to an outbox table in the same transaction as the change that causes them. A relay claims rows with `FOR UPDATE SKIP LOCKED`, publishes them to the topic exchange `integration.events` with publisher confirms, and marks a row only after the broker confirmed it. A crash in between publishes the event twice, never zero times.
- Consumers handle each message once: the handler's changes and an inbox row keyed by message id are saved in one transaction, so a redelivery is recognised and acknowledged. A message that fails is retried once and then dead-lettered (`greenhaus.events.dead`) instead of blocking the queue.
- Greenhaus publishes `device.claimed.v1` and `limit.reached.v1`, and consumes `entitlement.changed.v1`, `device.allocated.v1`, `device.retired.v1` and `command.completed.v1`. An entitlement event older than the stored one is ignored, because delivery order is not guaranteed. The order a device was allocated to is kept, so the claim event names it. A device built at home has none and the event omits it.
- Every event carries the headers of the contract (`messageId`, `occurredAt`, `schemaVersion`, `traceparent`), and the trace continues across the broker into the consumer. Tests validate payloads and headers against the AsyncAPI document.
- OpenTelemetry (traces, metrics) is exported when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The service adds spans for publishing and handling, and counters for published, handled, duplicate and dead-lettered events, limits reached and devices claimed.
- The same contract validator that checks the mock checks the service: every response in the test walk is verified against `greenhaus-api.yaml`, and the walk fails if an operation is not exercised.
- Tests run against a real PostgreSQL and a real RabbitMQ started by Testcontainers.

## Consequences
- The web app can switch from the mock to the service without a code change.
- The contract walk now covers every operation, including claim and the controls.
- Not yet verified against a running Pulse. The wire format is pinned against the contract by hand, so a first run against the real service is the next check.
- The tests need Docker.