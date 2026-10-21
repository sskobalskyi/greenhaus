# Greenhaus mock server

A .NET mock of the Greenhaus API that implements `contracts/openapi/greenhaus-api.yaml`, so the web UI, Storybook and other services can be built before the real services exist. Mocks live in the backend on purpose: clients talk to a real HTTP server that follows the contract, instead of carrying hand-written JSON.

It is not a stub that returns fixed files. It applies the same rules the real service will:

- **Tier limits:** pots per tier, the zone grid size, and how automated each metric may be.
- **Guards:** a heater cannot run in `auto` without a declared thermal cutoff, a sensor must be calibrated before `auto`, safety limits can only be tightened within the controller's hard limits, and the kill switch blocks runs.
- **Zones, never averages:** status is per zone, and the pot is as bad as its worst zone.
- **Requirements from plants:** the suggestion is the intersection of the plants' ranges, and disagreeing plants produce a conflict.
- **Claims:** unshipped, expired, wrong-token and already-claimed devices are refused with stable codes.

## Run

```powershell
docker compose up --build            # from the repository root, serves http://localhost:8090
# or
dotnet run --project Greenhaus.MockServer --urls http://localhost:8090
```

The base path is `/api/greenhaus/v1`, as in the contract. The contract itself is served at `/contracts/greenhaus-api.yaml`.

## Trying things out

The mock does not check tokens. Two request headers change what it returns:

| Header | Values | Effect |
| --- | --- | --- |
| `X-Mock-Tier` | `novice`, `gardener`, `druid` | Acts as that tier (default `gardener`) |
| `X-Mock-Scenario` | `grant-at-risk` | Adds a purchase grant in the at-risk state to the entitlements |

Seeded data:

| Pot | What it shows |
| --- | --- |
| Kitchen herbs | A simple pot, calibrated sensors, a pump in `auto`, everything in range |
| Balcony bed | A 2x2 zone grid: one dry zone, one alkaline zone, uncalibrated soil sensors, a heater without a thermal cutoff |
| Window sill | Basil and a cactus: the requirements suggestion reports a soil moisture conflict |

Devices to claim: `ghx-demo-0001` with token `demo-token` (claimable), `ghx-demo-0002` with `demo-token-2` (not shipped), `ghx-demo-0003` with `expired-token` (expired).

For end-to-end tests the mock also has `POST /mock/reset`, which reseeds its in-memory state. It is a test aid and not part of the contract.

## Contract tests

```powershell
dotnet test
```

Every response is checked against the OpenAPI document: the status must be documented for the operation and the body must match the schema, including formats and enums. One test walks every operation in the contract and fails if the mock does not serve one of them, so the contract and the mock cannot drift apart. A self-test proves the validator rejects wrong bodies and undocumented statuses.

The same checks are meant to run against the real services later.

## Not yet covered

The billing API and the Pulse device API have contracts but no mock yet.
