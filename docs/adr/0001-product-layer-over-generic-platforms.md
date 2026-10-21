# 1. A product layer over generic platforms

Status: accepted

## Context
The product needs a shop, device telemetry with alerts, and a knowledge assistant. Orderflow, Pulse and Docs-Brain already exist as standalone, generic projects. Putting plant concepts (pots, plants, requirements, tiers) into them would turn each into a part of one product and stop them being reusable or independently runnable.

## Decision
Greenhaus is a separate layer. It holds all product concepts and composes the platforms:

- Orderflow stays a generic marketplace. It publishes order events and knows nothing about sensors.
- Pulse stays a generic device platform: devices, channels, capabilities, per-device rules, commands, readings. It knows nothing about pots.
- Docs-Brain stays a generic retrieval and assistant service.
- Between projects there are only events (RabbitMQ), device messages (MQTT) and public HTTP APIs. There are no shared databases and no shared code beyond the contracts in this repository.
- Every project must still run on its own with `docker compose up`.

The contracts live here, in `contracts/`, written first. A mock backend implements them so clients can be built before the services exist, and contract tests keep mock and real services honest.

## Consequences
- Each platform remains a standalone portfolio piece, and the product shows how generic parts compose into something specific.
- More integration surface to specify and test, which is why the contracts come first.
- Cross-project changes land as a contracts change first, then as pull requests in each repository.
