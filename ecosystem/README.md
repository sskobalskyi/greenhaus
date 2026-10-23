# The ecosystem on one machine

Orderflow (the shop), Pulse (devices) and the Greenhaus services run together on **one broker** and **one Keycloak realm**, and one script checks the whole story for a new person:

> buy a kit in the shop -> the purchase grants a tier -> a device is allocated and registered in Pulse -> the order ships and the device becomes claimable -> the person claims it into a pot -> readings show in the pot -> the order shows as in use.

```powershell
./ecosystem/up.ps1      # first run exports Orderflow and Pulse from origin/main, builds and starts everything (several minutes)
./ecosystem/e2e.ps1     # the end-to-end check, about a minute, exits 0 when every step holds
./ecosystem/down.ps1    # stop (add -Volumes to delete the data)
```

Needs Docker and PowerShell. `up.ps1 -Refresh` exports the projects again from the latest `origin/main`.

## How it fits together

| Piece | Where | Notes |
| --- | --- | --- |
| Keycloak, realm `greenhaus` | `localhost:8181` | One identity for everyone, [ADR 7](../docs/adr/0007-shared-identity.md) |
| RabbitMQ | `localhost:5672` (management `15672`) | Orderflow's broker is the one broker; Pulse and Greenhaus connect to it. Their own brokers do not start |
| Orderflow gateway, web | `localhost:8080`, `localhost:5173` | Sells the kits from `data/shop-catalog.json` with the stock in `ecosystem/stock.json` |
| Pulse API | `localhost:5080` | With events on, on the shared broker. Its dashboard is not started (it would claim 8080) |
| Greenhaus API, billing, fulfilment | `8091`, `8092`, `8093` | Take their Pulse token from the shared realm |

The compose files of each project are used as they are, plus one small file each in this folder (`orderflow.yml`, `pulse.yml`, `greenhaus.yml`) that points them at the shared broker and seeds the shop. Orderflow and Pulse belong to other repositories, so the script builds them from a clean export of their `main` branch under `../.ecosystem`, never from a working tree that may hold unfinished work.

## What the check proves

Each step asserts something another project must have done: the order's customer is the same Keycloak subject the Greenhaus API sees, billing's grant reaches that subject as a Gardener tier, fulfilment's `device.allocated.v1` makes Pulse register the device with the kit's channels and actuators, the shop's `order.shipped.v1` makes fulfilment ship it in Pulse, the claim binds the device to the person in Pulse and links it to a pot, a reading sent to Pulse shows in the pot's status, and the claim event marks the order as in use in the shop.

## Known limits

- It is a manual check, not part of CI: it needs three repositories and about ten containers.
- Passwords, the client secret and the realm are development data.
- Both Orderflow and Pulse use their own Keycloak on port 8180 when run alone, so they cannot run side by side that way. Here neither starts it.