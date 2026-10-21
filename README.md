# Greenhaus

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

Early. The first stream is the contract layer: HTTP APIs in OpenAPI, events and MQTT topics in AsyncAPI, and a mock backend that serves them so the UI and the services can be built in parallel.

## Layout

```
contracts/openapi/     HTTP contracts: Greenhaus API, billing API, Pulse device API
contracts/asyncapi/    integration events (RabbitMQ) and device topics (MQTT)
data/                  curated data, such as the species catalog
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

## Licence

MIT, see [LICENSE](LICENSE).
