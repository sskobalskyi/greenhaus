# 4. Sign-in with Keycloak

Status: accepted

## Context
The product needs sign-up and sign-in (self-registration, decision D8). The API already scopes everything to the token's subject, and the web app so far ran against a mock with no login.

## Decision
- A `greenhaus` realm is imported from `auth/greenhaus-realm.json`: self-registration on, English and Ukrainian, a public client `greenhaus-web` (authorization code with PKCE S256) and a bearer-only `greenhaus-api`. The web client adds the audience `greenhaus-api` to access tokens, and the API requires it.
- The web app uses `oidc-client-ts`. Sign-in is on only when `VITE_OIDC_AUTHORITY` is set, so the mock workflow and the end-to-end tests are unchanged. The app asks Keycloak for the app's language (`ui_locales`), so registration and login speak it too.
- Nothing of the app, and none of its requests, appears before a person is signed in. The set-up guide stays open. The plans page needs the person's tier, so it is behind sign-in too; showing prices before sign-up would need a public variant.
- Tokens stay in the browser session storage of the OIDC client and travel in the `Authorization` header, so the API allows no cookies or credentials in CORS.
- Inside Docker the API reaches Keycloak by its service name while tokens carry the public address, so the API takes an `Auth:Issuer` for the issuer check. Keycloak runs with a fixed hostname and dynamic back-channel URLs.
- Signing out marks the app as leaving before it ends the Keycloak session. Without it the app noticed the missing user first and sent the person straight back to Keycloak, which still had the session and signed them in again.
- Keycloak runs only in `docker-compose.auth.yml`, as a dev-mode container with a development admin. It is a local convenience, not a deployment.

## Consequences
- The real stack is usable by a person in a browser: register, sign in, see their own pots, sign out.
- Pulse's own Keycloak is a separate realm and container. Sharing one realm across the projects (the ecosystem plan's single-identity goal) is a later step for the umbrella compose.
- Sign-in is checked by hand in a real browser (registration, callback, API call with the token, Ukrainian login page, sign-out). An automated end-to-end test would need Keycloak in CI.