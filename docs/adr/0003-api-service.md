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
- Readings and commands go through two seams, `IReadingSource` and `ICommandSender`. Until the Pulse client is wired, status reports nothing measured and a run command is accepted and goes nowhere.
- Events are written to an outbox table in the same transaction as the change that causes them. A relay publishes them to RabbitMQ, so a broker outage never loses an event.
- The same contract validator that checks the mock checks the service: every response in the test walk is verified against `greenhaus-api.yaml`, and the walk fails if an operation is neither exercised nor listed as waiting for a controller.
- Tests run against a real PostgreSQL started by Testcontainers.

## Consequences
- The web app can switch from the mock to the service without a code change.
- Operations that need a linked controller (claim, run, mode, limits, calibration) are covered when the Pulse client lands, and the exclusion list in the test shows exactly which.
- The tests need Docker.