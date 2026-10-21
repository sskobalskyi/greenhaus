# 2. One web workspace, the design system consumed from source

Status: accepted

## Context
The design system (`packages/ui`) and the product app (`apps/web`) need one copy of React, i18next and the shadcn components, and a change to a component should show up in the app straight away. Publishing the design system as a package is overkill for one consumer.

## Decision
- The repository is an npm workspace with `packages/ui` (`@greenhaus/ui`) and `apps/web` (`@greenhaus/web`), one lockfile at the root.
- The app imports the design system from source. `@greenhaus/ui` exports its TypeScript entry point and its stylesheet, and Vite and Tailwind compile them as part of the app. Tailwind is told to scan both packages.
- Design system files import their own modules with `@/...`, so in the app `@` points at the design system and `~` at the app's own source.
- The design system owns the i18next instance. The app adds its own strings to it, so there is one language switch and one set of plural rules.
- The app talks only to the Greenhaus API contract. In local development that is the mock backend (`docker compose up mock`). The mock offers two developer aids that are not part of the contract: the `X-Mock-Tier` header to act as another tier, and `POST /mock/reset` to reseed it for end-to-end tests.
- Plans are shown with limits from `GET /tiers` and prices from the billing service. A paid tier with no known price says so, and is never labelled free.

## Consequences
- One command installs everything, and a component change is visible in Storybook and in the app at once.
- The app cannot be built without the design system source next to it. If a second consumer appears, `@greenhaus/ui` can be built and published without changing how the app imports it.
- End-to-end tests need the mock running. They reset it before each test and run one at a time because its state is in memory.