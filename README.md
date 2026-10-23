# Greenhaus

[![Contracts](https://github.com/sskobalskyi/greenhaus/actions/workflows/ci.yml/badge.svg)](https://github.com/sskobalskyi/greenhaus/actions/workflows/ci.yml)
![Status: alpha](https://img.shields.io/badge/status-alpha-orange)

> Working name. Not affiliated with any existing Greenhaus business, and the name is not licensed under the MIT licence of this repository.

Smart pots for plants: sensors and relays in a pot controller, a shop to buy them, a QR scan to claim them, live monitoring and gentle automation, and an assistant that knows your plants. Greenhaus is the **product layer** that composes three generic platforms:

| Platform | Role | Repository |
| --- | --- | --- |
| Orderflow | Marketplace: catalog, orders, payment, shipping | `orderflow` |
| Pulse | Device platform: telemetry, alerts, commands | `pulse` |
| Docs-Brain | Knowledge and AI: retrieval, assistant | `docs-brain` |
| Observatory | Observability lab: traces, metrics, dashboards, load tests | `observatory` |

Greenhaus owns everything that is specific to plants: users and tiers, pots, zones, plants, requirements, automation policy, care tasks, subscriptions and achievements. The platforms stay generic and know nothing about pots ([ADR 0001](docs/adr/0001-product-layer-over-generic-platforms.md)).

## Status

**Alpha.** The services below run and are tested, but the project is a local demo, not a deployment. Work lands through pull requests.

What exists: the contracts (OpenAPI and AsyncAPI) with a contract-tested mock; the Greenhaus API, billing and fulfilment services on PostgreSQL, RabbitMQ and Keycloak; the design system and the product app; and a check that runs the whole story across Orderflow, Pulse and these services (`ecosystem/`).
## Layout

```
contracts/openapi/     HTTP contracts: Greenhaus API, billing API, Pulse device API
contracts/asyncapi/    integration events (RabbitMQ) and device topics (MQTT)
mock-server/           .NET mock of the Greenhaus API with contract tests ([details](mock-server/README.md))
packages/ui/           design system and Storybook on shadcn/ui ([details](packages/ui/README.md))
apps/web/              the product app ([details](apps/web/README.md))
data/                  curated data: the species catalog and the kits for the shop (`shop-catalog.json`)
docs/adr/              architecture decision records
```

## Contracts

Contract-first: the specifications are the source of truth, and both mocks and real services are checked against them.

| Contract | File |
| --- | --- |
| Greenhaus API: profile, tiers, pots, zones, plants, requirements, controls, care tasks, claim | [`greenhaus-api.yaml`](contracts/openapi/greenhaus-api.yaml) |
| Billing API: plans, subscription, purchase grants | [`billing-api.yaml`](contracts/openapi/billing-api.yaml) |
| Pulse device API additions: devices, channels, capabilities, rules, commands, claim | [`pulse-device-api.yaml`](contracts/openapi/pulse-device-api.yaml) |
| Integration events between projects | [`integration-events.yaml`](contracts/asyncapi/integration-events.yaml) |
| Device topics and payloads over MQTT | [`device-mqtt.yaml`](contracts/asyncapi/device-mqtt.yaml) |

Validate locally:

```powershell
npx --yes @redocly/cli lint contracts/openapi/*.yaml
npx --yes @asyncapi/cli validate contracts/asyncapi/integration-events.yaml
npx --yes @asyncapi/cli validate contracts/asyncapi/device-mqtt.yaml
```

## The app

React app for pots, plants, controls, plans and the QR claim flow, in English and Ukrainian, light and dark. It runs against the mock backend today.

![Pots overview](docs/screenshots/app/pots-light-en.png)

![Controls, light English](docs/screenshots/app/controls-light-en.png)

```powershell
npm install                    # installs the web workspace
docker compose up -d mock      # the mock backend on :8090
npm run dev -w @greenhaus/web  # http://localhost:5174
npm run e2e -w @greenhaus/web  # 38 end-to-end tests with an accessibility scan of every page
```

See [apps/web](apps/web/README.md) and [ADR 0002](docs/adr/0002-web-workspace-and-design-system-from-source.md).

## Design system

A shadcn/ui based component library with a Storybook, in English and Ukrainian, light and dark. Every story runs as a browser test with an accessibility check, and live-data stories read from the mock backend.

| | |
| --- | --- |
| ![Zone map](docs/screenshots/ui/pot-zone-map-light-en.png) | ![Actuator control, dark Ukrainian](docs/screenshots/ui/actuator-control-locked-dark-uk.png) |

![Tiers in Ukrainian, dark](docs/screenshots/ui/tier-cards-dark-uk.png)

See [packages/ui](packages/ui/README.md).

## Run the mock

```powershell
docker compose up --build   # http://localhost:8090/api/greenhaus/v1
dotnet test mock-server/GreenhausMock.slnx   # checks every response against the contract
```

## Run the real service

`src/Greenhaus.Api` is the real implementation of the same contract: ASP.NET Core on PostgreSQL (EF Core), with the tier rules in `src/Greenhaus.Domain`. See [ADR 3](docs/adr/0003-api-service.md).

```powershell
docker compose --profile real up --build api   # http://localhost:8091/api/greenhaus/v1
dotnet test tests/Greenhaus.Domain.Tests
dotnet test tests/Greenhaus.Api.Tests          # real PostgreSQL and RabbitMQ through Testcontainers, every response and event checked against the contracts
```

Locally the service runs with `Auth__Mode=Development`, which trusts an `X-User-Id` header. Production-style use validates Keycloak tokens (`Auth__Authority`, `Auth__Audience`). Controllers are linked, read and commanded through Pulse's device API (`Pulse__BaseUrl`, with a client-credentials token or `Pulse__StaticToken` for local runs). Claiming a controller creates its sensors and actuators, tightens defaults inside the controller's hard limits, and syncs alert rules to Pulse whenever requirements change. Events go through RabbitMQ (`RabbitMq__Host`): the outbox is published with confirms, and entitlement, allocation, retirement and command-result events are consumed idempotently. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces and metrics.

### Real sign-in with Keycloak

```powershell
docker compose -f docker-compose.yml -f docker-compose.auth.yml --profile real up -d --build api keycloak
$env:VITE_OIDC_AUTHORITY = "http://localhost:8181/realms/greenhaus"; $env:VITE_MOCK = "false"
$env:VITE_API_BASE = "http://localhost:8091/api/greenhaus/v1"
$env:VITE_BILLING_BASE = "http://localhost:8092/api/billing/v1"   # the real billing service instead of the mock
npm run dev -w @greenhaus/web   # http://localhost:5174
```

The app sends a signed-out person to Keycloak (in the app's language), where anyone can register (or use `demo` / `demo`). The API validates the token's issuer and the audience `greenhaus-api`, and `/me` is created from the token on the first call. With the real billing, choosing a plan subscribes through it (a demo payment method, nothing real is charged), and the new tier reaches the Greenhaus API through billing's event, so the app looks again for a few seconds. The plans and the set-up guide can be read without an account; everything else asks for one. Sign-out ends the Keycloak session. See [ADR 4](docs/adr/0004-sign-in.md). The same realm also holds the clients of Orderflow and Pulse, so one account works across the projects ([ADR 7](docs/adr/0007-shared-identity.md)).

The sign-in flow is covered by an automated browser test against this stack (a visitor reads the plans, is sent to Keycloak, registers and comes back signed in, sees the language of the app on the Keycloak pages, and sign-out really ends the session):

```powershell
docker compose -f docker-compose.yml -f docker-compose.auth.yml --profile real up -d --build api keycloak
docker compose up -d mock                     # for the prices on the plans page
npm run e2e:auth -w @greenhaus/web            # starts the web app itself and waits for the stack
```

### The pot assistant

`POST /pots/{potId}/assistant/ask` asks the Docs-Brain assistant about a pot and streams the answer. The tier decides the monthly quota (10, 100, 500; only answered questions count) and how much the assistant is told: the pot and its plants, plus the latest readings and alerts, plus the last days and the logs. It needs Docs-Brain (its `main`, with the mock provider offline) and a token source:

```powershell
$env:DOCSBRAIN_URL = "http://host.docker.internal:5090"
$env:DOCSBRAIN_TOKEN_URL = "http://host.docker.internal:8181/realms/greenhaus/protocol/openid-connect/token"
$env:DOCSBRAIN_CLIENT_ID = "greenhaus-assistant"; $env:DOCSBRAIN_CLIENT_SECRET = "greenhaus-assistant-dev-secret"
```

Without them the endpoint answers `503 assistant_unavailable`. See [ADR 9](docs/adr/0009-pot-assistant.md).

### Against a live Pulse

Checked against Pulse `main` (device registry, JWT, channels) with its Keycloak: Greenhaus asks for a client-credentials token of `pulse-service` and claims a shipped device, reads its state and latest readings, and stores calibration. Rules and commands are in the Pulse contract but not in Pulse yet, so syncing alert rules and sending run or kill-switch commands answers `pulse_not_supported`: the pot's own data stays correct, a run is refused with 503 and the rules follow when Pulse offers them.

```powershell
# in a checkout of Pulse
docker compose -f docker-compose.yml -f docker-compose.auth.yml up -d --build redpanda timescaledb keycloak api
# in this repository
$env:PULSE_TOKEN_URL = "http://host.docker.internal:8180/realms/pulse/protocol/openid-connect/token"
$env:PULSE_CLIENT_ID = "pulse-service"; $env:PULSE_CLIENT_SECRET = "pulse-dev-secret"
docker compose --profile real up -d --build api
```

### The billing service

`src/Greenhaus.Billing` implements `billing-api.yaml` on its own PostgreSQL: public plans, subscribe and cancel with a demo payment token (`...-declined` is refused, `...-renewal-fails` fails its first renewal), renewals, invoices, and **purchase grants** from Orderflow events (`order.confirmed.v1` of $30, $80 or $150 earns 3 or 12 months of Gardener or 6 of Druid; a return or refund puts it at risk for 7 days, then it is revoked). It publishes `entitlement.changed.v1`, `grant.status_changed.v1` and `subscription.changed.v1`, which the Greenhaus API already consumes. `Billing:Period` and `Billing:Grace` shorten time for demos. See [ADR 5](docs/adr/0005-billing-service.md).

```powershell
docker compose --profile real up --build billing   # http://localhost:8092/api/billing/v1, with RabbitMQ
dotnet test tests/Greenhaus.Billing.Tests          # real PostgreSQL, every response checked against the contract
```

### Fulfilment: from an order to devices

`src/Greenhaus.Fulfilment` turns a confirmed order of kits into devices. For every kit unit it allocates a device (an id, a one-time claim token, a broker password) from `data/kit-capabilities.json` and publishes `device.allocated.v1` with hashes only. When the order ships it tells Pulse the devices can be claimed (retrying while Pulse has not registered them yet), and when the order is returned or refunded before shipping it retires them. The people who pack the boxes read the claim token, claim link and password from `GET /api/fulfilment/v1/orders/{orderId}/allocations` ([contract](contracts/openapi/fulfilment-api.yaml)); they are erased once Pulse accepts the shipment. See [ADR 6](docs/adr/0006-fulfilment-service.md).

```powershell
docker compose --profile real up --build fulfilment   # http://localhost:8093/api/fulfilment/v1, needs rabbitmq and a Pulse (PULSE_URL, PULSE_TOKEN)
dotnet test tests/Greenhaus.Fulfilment.Tests          # real PostgreSQL and RabbitMQ, a fake Pulse, responses and events checked against the contracts
```

### The whole ecosystem

`./ecosystem/up.ps1` starts Orderflow, Pulse and the Greenhaus services together on one broker and one Keycloak realm, and `./ecosystem/e2e.ps1` runs the whole story for a new person, from buying a kit to a reading in the pot. See [ecosystem/README.md](ecosystem/README.md) and [ADR 8](docs/adr/0008-ecosystem-orchestration.md).

### The kits in the shop

`data/shop-catalog.json` lists three kits (starter module, pot kit, greenhouse kit) in the seed format of Orderflow's catalog, in English and Ukrainian. Start Orderflow with `Catalog:SeedFile` pointing at it to sell them. The prices ($30, $80, $150) match the placeholder purchase-grant thresholds of the spec. Orderflow stays generic: it knows SKUs, prices and names, not what is in the box. What is in the box is `data/kit-capabilities.json`: for each SKU the model, the sensor channels and the actuators with their hard limits and (for a heater) the declared thermal cutoff, in the shape of the Pulse device API. The fulfilment step that allocates a device for an ordered kit reads it. CI checks it against the catalog and the Pulse metric and actuator names.

## Roadmap


Done: the contracts and the mock; the design system and the product app (with Keycloak sign-in, public plans and the real billing); the **Greenhaus API** (pots, plants, zones, requirements, tiers, controls, care, claims), **billing** (plans, subscriptions, invoices, purchase grants with the grace period) and **fulfilment** (device allocation, shipping, retirement), all contract-tested against real PostgreSQL and RabbitMQ; one shared Keycloak realm; the kits in the shop; and the end-to-end story across the projects, including the return branch.

Still to do:

- **Pulse:** live readings and commands over MQTT (the app polls every ten seconds, and Pulse has no rules or commands endpoints yet, so run and kill-switch commands answer `pulse_not_supported`).
- **Docs-Brain:** the assistant tab in the app is built against the mock (streamed answer, sources, quota, what the plan lets it see). The real service endpoint and the Docs-Brain integration are next.
- **Billing:** a returned order cannot be un-returned (the contract has no event for it), and a subscription that stays `past_due` is not cancelled automatically.
- **Orderflow:** nothing open on the Greenhaus side.
- **Later:** achievements (last, by decision), encrypting the packing secrets at rest, an ESP32 sketch for the MQTT topics.
## Licence

MIT, see [LICENSE](LICENSE).
