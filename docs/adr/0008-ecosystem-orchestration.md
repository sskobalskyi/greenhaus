# 8. Running the ecosystem together

Status: accepted

## Context
The projects are separate repositories with their own compose files, ports and Keycloak. The story across them (buy, grant, allocate, ship, claim, read, activate) only holds if the pieces really run together, and each project's own tests cannot show that.

## Decision
- Greenhaus is the umbrella: `ecosystem/` holds a start script, a stop script and one end-to-end script, plus one override file per project. No project gets code or its compose files changed for this.
- One broker: Orderflow's RabbitMQ is the broker of the ecosystem. Pulse and the Greenhaus services point at it (`host.docker.internal:5672`), their own brokers do not start. One Keycloak: the shared realm of ADR 7.
- Orderflow and Pulse are built from a clean export of `origin/main` (`git archive`), because other sessions work in their working trees. `-Refresh` re-exports.
- The shop sells the kits through Orderflow's own seed files (`data/shop-catalog.json`, `ecosystem/stock.json`), so Orderflow stays generic.
- The end-to-end script creates a new person in Keycloak each run, so runs do not depend on each other, and talks to every service with a real token of that realm. It asserts the hand-offs between projects, not their internals.
- It is a manual check, not a CI job: it needs three repositories and many containers.

## Consequences
- One command starts a working ecosystem, and one command shows whether the story still holds after a change in any project.
- A change in a project's compose service names or ports can break the overrides, which the script then shows.
- Pulse's dashboard and Orderflow's own Keycloak are left out, which keeps the ports free of clashes.