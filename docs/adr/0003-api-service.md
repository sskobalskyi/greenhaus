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
- Events are written to an outbox table in the same transaction as the change that causes them. A relay publishes them to RabbitMQ, so a broker outage never loses an event.
- The same contract validator that checks the mock checks the service: every response in the test walk is verified against `greenhaus-api.yaml`, and the walk fails if an operation is not exercised.
- Tests run against a real PostgreSQL started by Testcontainers.

## Consequences
- The web app can switch from the mock to the service without a code change.
- The contract walk now covers every operation, including claim and the controls.
- Not yet verified against a running Pulse. The wire format is pinned against the contract by hand, so a first run against the real service is the next check.
- The tests need Docker.