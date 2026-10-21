# Greenhaus mock server

A .NET mock that implements three contracts on one host, so the web UI, Storybook and the services can be built before the real services exist:

| API | Contract | Base path |
| --- | --- | --- |
| Greenhaus | `contracts/openapi/greenhaus-api.yaml` | `/api/greenhaus/v1` |
| Billing | `contracts/openapi/billing-api.yaml` | `/api/billing/v1` |
| Pulse device API | `contracts/openapi/pulse-device-api.yaml` | `/api/pulse/v2` |

Mocks live in the backend on purpose: clients talk to a real HTTP server that follows the contract, instead of carrying hand-written JSON. It is not a stub that returns fixed files. It applies the rules the real services will, and the three mocks agree with each other (subscribing in billing changes the Greenhaus entitlements).

## What it knows

**Greenhaus**
- Tier limits (pots, zone grid size, how automated each metric may be), heater and calibration guards, safety limits only tightened within the controller's hard limits, and the kill switch.
- Status per zone, never averaged. Requirements as the intersection of plant ranges, with conflicts.
- Claim checks with stable codes.

**Billing**
- Public plans, subscribe, change and cancel. A token ending `-declined` is refused with 402, one ending `-renewal-fails` is accepted and then fails on renewal (the subscription becomes `past_due`).
- A subscription lasts 30 days and renews on the mock clock. Cancelling keeps the plan until the period ends.
- Purchase grants with a 7 day grace period when the order is returned: `active`, `at_risk`, `revoked`. The effective tier is the best of the subscription and the grants that still count.

**Pulse device API**
- A registry with the full lifecycle (`allocated`, `shipped`, `claimed`, `active`, `retired`). Registration reports every problem at once and is idempotent.
- Claim with a one-time token stored as a SHA-256 hash. A wrong token is reported before an expired one, the token works once, and attempts are rate limited (5 a minute per device, 30 per caller, `429` with `Retry-After`).
- Per-device rules, calibration, and commands that are idempotent on their id and expire. A simulated controller carries them out and refuses what exceeds its own hard limits, a run too soon after the last, the daily cap, or anything while the kill switch is on. Commands for an offline device wait and expire if it never returns.
- Series with a window limit per resolution, and `X-Pulse-Truncated` when a raw series is cut.
- Callers: service by default, or a user with `X-Mock-Caller: user:<subject>`, who sees only their own devices (others answer 404) and gets 403 on service operations.

## Run

```powershell
docker compose up --build            # from the repository root, serves http://localhost:8090
# or
dotnet run --project Greenhaus.MockServer --urls http://localhost:8090
```

The contracts are served at `/contracts/<file>`.

## Trying things out

The mock does not check tokens. Request headers change what it returns:

| Header | Values | Effect |
| --- | --- | --- |
| `X-Mock-Tier` | `novice`, `gardener`, `druid` | The Greenhaus API acts as that tier. Without it the tier comes from the billing mock |
| `X-Mock-Scenario` | `grant-at-risk` | Adds a purchase grant in the at-risk state to the Greenhaus entitlements |
| `X-Mock-Caller` | `service` (default), `user:<subject>` | Who calls the Pulse device API |

Mock-only controls, which are not part of any contract:

| Call | Effect |
| --- | --- |
| `POST /mock/reset` | Reseeds everything and resets the clock |
| `POST /mock/clock/advance` `{ "days": 31 }` | Moves the mock clock, so a subscription renews, a grace period ends or a command expires |
| `POST /mock/billing/grants` `{ "tier": "druid", "months": 3 }` | Stands in for `order.confirmed.v1`: a purchase grants a plan |
| `POST /mock/billing/grants/{id}/return` and `.../restore` | Stands in for `order.returned.v1` and for the return being cancelled |
| `POST /mock/pulse/devices/{id}/reading` | The device reports: a claimed device becomes active and online |
| `POST /mock/pulse/devices/{id}/silence` | The device goes quiet and turns offline |

Seeded data. Greenhaus pots: **Kitchen herbs** (simple, calibrated, pump in `auto`), **Balcony bed** (2x2 zones, a dry zone, an alkaline zone, an uncalibrated soil sensor, a heater without a thermal cutoff) and **Window sill** (basil and a cactus disagree about moisture). The demo user starts on a paid Gardener plan. Pulse devices: `ghx-pot-kitchen` and `ghx-bed-balcony` (active, owned by `demo-user`), `ghx-demo-0001` (shipped, token `demo-token`), `ghx-demo-0002` (allocated, token `demo-token-2`) and `ghx-demo-0003` (shipped, token `expired-token`, expired).

## Contract tests

```powershell
dotnet test
```

Every response is checked against its OpenAPI document: the status must be documented for the operation and the body must match the schema, including formats and enums. Authentication failures (`401`, `403`) are described once in each contract's security scheme, so a secured operation may answer them with a problem document. One test per API walks every operation in its contract and fails if the mock does not serve one, so the contracts and the mock cannot drift apart. Self-tests prove the validator rejects wrong bodies and undocumented statuses. The rule tests cover declined and failing cards, renewal and cancellation, grants at risk, claim states and rate limits, user visibility, commands (hard limits, expiry, idempotency, offline devices) and series windows.

The same checks are meant to run against the real services later.