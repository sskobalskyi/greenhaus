# 7. One identity for all projects

Status: accepted

## Context
A person who buys a kit in Orderflow, claims it in Greenhaus and sees its readings in Pulse is one person. Billing grants a purchase to the customer id of the order event, which is the Orderflow Keycloak subject, and the Greenhaus API scopes everything to the subject of its own token. With three Keycloaks and three realms those are three different ids, so a purchase granted a tier to an id nobody signs in with.

## Decision
- One realm, `greenhaus`, served by the Keycloak of `docker-compose.auth.yml` on port 8181, is the identity of the whole ecosystem. A person registers once (self-registration, English and Ukrainian), and the Keycloak subject is the same in every project: Orderflow's `customerId`, billing's user id and the Greenhaus API's user id.
- The realm holds every client the projects need, with the settings they had in their own realms, so the projects change configuration and no code:
  - `greenhaus-web` (public, PKCE) and `greenhaus-api` (audience of the API);
  - `orderflow-web` (public, PKCE, redirect `http://localhost:5173/*`) with the `realm roles` mapper that writes the `roles` claim Orderflow's gateway reads, and the realm role `admin`;
  - `pulse-web` (public) and `pulse-service` (client credentials, secret `pulse-dev-secret`) with the realm role `pulse-service`, which is how Pulse recognises a service caller (and how the Greenhaus services call it);
  - users `demo`, `alice`, `bob` (password = name), `admin` (role `admin`).
- Each project points at the shared realm through configuration it already has:
  - Orderflow gateway: `Auth__MetadataAddress=http://<keycloak>:8080/realms/greenhaus/.well-known/openid-configuration`, `Auth__Issuer=http://localhost:8181/realms/greenhaus`; Orderflow web: `VITE_OIDC_AUTHORITY=http://localhost:8181/realms/greenhaus`.
  - Pulse API: `Auth__Authority`/`Auth__Issuer` the same way; its service callers use the realm's `pulse-service` client.
- The own Keycloak of Orderflow and Pulse stays as their standalone default, so each project still starts alone. The shared realm is what the umbrella setup uses.
- Audience is not required by Orderflow and Pulse (they validate the issuer and signature). The Greenhaus API requires the audience `greenhaus-api`, which only `greenhaus-web` tokens carry.

## Consequences
- Purchase grants reach the person who bought: the order's customer id is the subject the Greenhaus API sees after sign-in.
- Checked with real tokens from the realm: the same subject across clients, `roles: admin` from `orderflow-web`, `realm_access.roles: pulse-service` from the service client.
- The realm file is development data with fixed passwords and a fixed client secret. It is not a deployment.
- Orderflow and Pulse still ship their own realm files and ports (both use 8180, so they cannot run together without one of them being re-pointed). Re-pointing them is a change in their own repositories.