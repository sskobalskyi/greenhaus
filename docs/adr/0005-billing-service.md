# 5. The billing service

Status: accepted

## Context
Billing owns paid subscriptions and, later, grants earned by purchases, so that Orderflow stays independent of Greenhaus (ADR 1). Until now it only existed as a part of the mock. The contract is `contracts/openapi/billing-api.yaml`, and the Greenhaus API already asks billing's tier questions through events (ADR 3).

## Decision
- `Greenhaus.Billing` is a separate ASP.NET Core service with its own PostgreSQL database (`billing` schema, migrations on startup) and its own container. It shares only the pure rules with the rest of the repository, in `Greenhaus.Domain`: prices, the grant thresholds and how the effective entitlement is computed.
- It implements the whole contract: plans (public), subscribe and cancel with a demo payment token, subscription lifecycle, invoices and grants (read only). The demo provider declines a token ending in `-declined` (402, kept as a failed invoice). A token ending in `-renewal-fails` is accepted now and fails at the next renewal, which makes the subscription `past_due`.
- Time is a `TimeProvider`, and a period and a grace period are configuration (`Billing:Period`, `Billing:Grace`), the compressed demo clock of the contract. The same `BillingProcessor` brings state up to the clock: it runs for one user whenever they call the API, so an answer is never stale, and for everyone on a timer (`BillingTicker`). Tests move a fake clock and ask.
- Authentication is Keycloak JWT with the same development mode as the API. Everything is scoped to the token's subject. CORS allows the dev web origins, with no credentials.
- Tests run against a real PostgreSQL and check every response against the OpenAPI document. The contract walk fails if an operation is not exercised.

## Consequences
- The web app can use the real billing by pointing `VITE_BILLING_BASE` at `http://localhost:8092/api/billing/v1`.
- Subscribing replaces the single subscription row of a user, so history lives in the invoices.
- Renewal catches up periods one by one, so a long absence produces one invoice per period. A subscription that stays `past_due` is not cancelled automatically: dunning is out of scope.