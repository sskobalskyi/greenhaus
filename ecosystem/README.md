# The ecosystem on one machine

Orderflow (the shop), Pulse (devices) and the Greenhaus services run together on **one broker** and **one Keycloak realm**, and one script checks the whole story for a new person:

> buy a kit in the shop -> the purchase grants a tier -> a device is allocated and registered in Pulse -> the order ships and the device becomes claimable -> the person claims it into a pot -> readings show in the pot -> the person asks the assistant, whose answer is grounded in that reading -> the order shows as in use.

```powershell
./ecosystem/up.ps1      # first run exports Orderflow and Pulse from origin/main, builds and starts everything (several minutes)
./ecosystem/e2e.ps1     # the end-to-end check, about a minute, exits 0 when every step holds
./ecosystem/down.ps1    # stop (add -Volumes to delete the data)
```

The story also has a branch where the person changes their mind: `./ecosystem/e2e.ps1 -CancelReturn` returns the goods, then cancels the return in the shop (`order.return_cancelled.v1`), and checks that the grant is active again with its original end date, the tier is kept and the order is shipped again.

The story also has a sad branch: `./ecosystem/e2e.ps1 -Return` returns the goods and checks the purchase grant goes to at risk with a deadline while the person keeps the tier. With `./ecosystem/up.ps1 -ShortGrace` (grace of 20 seconds instead of 7 days) `./ecosystem/e2e.ps1 -WaitForRevocation` also waits for the revocation, checks the tier falls back to Novice and that the pot and its device are kept.

To leave a demo account behind to look around in, run the story for a chosen person and keep the emulated controller running: `./ecosystem/e2e.ps1 -Username tester -Password 'Tester123!' -KeepRunning` (then start the web app with the settings in the root README, or open the shop on `localhost:5173`). The realm already has `demo`, `alice` and `bob` (password = name) and `admin` / `admin`; use another name than those.

Needs Docker and PowerShell. `up.ps1 -Refresh` exports the projects again from the latest `origin/main`.

## How it fits together

| Piece | Where | Notes |
| --- | --- | --- |
| Keycloak, realm `greenhaus` | `localhost:8181` | One identity for everyone, [ADR 7](../docs/adr/0007-shared-identity.md) |
| RabbitMQ | `localhost:5672` (management `15672`) | Orderflow's broker is the one broker; Pulse and Greenhaus connect to it. Their own brokers do not start |
| Orderflow gateway, web | `localhost:8080`, `localhost:5173` | Sells the kits from `data/shop-catalog.json` with the stock in `ecosystem/stock.json` |
| Pulse dashboard | `localhost:8085` | Live charts per device and channel, signs in with the shared realm (same people as the Greenhaus app, for example `tester`). Needs a Pulse `main` with PULSE_WEB_PORT (`up.ps1 -Refresh`) |
| Pulse API | `localhost:5080` | With events on, on the shared broker. Its dashboard is not started (it would claim 8080) |
| Greenhaus API, billing, fulfilment | `8091`, `8092`, `8093` | Take their Pulse token from the shared realm |
| EMQX (devices) | `localhost:1884` (dashboard `18084`) | Pulse's broker for devices, with Pulse's bridge and its per-device credentials (`docker-compose.mqtt.yml` and `docker-compose.mqtt-auth.yml` of the Pulse repository): a device connects with its id and the broker password from the packing bundle and may publish only under its own topic and subscribe only to its own commands |
| Docs-Brain API | `localhost:5090` | The assistant behind the Greenhaus API. Mock AI (no model download), accepts the service token of the shared realm (`docsbrain.env`). Its web UI is not started (it would claim 8090). `up.ps1` also writes one care text per species from `data/species.json` into its shared corpus, in English and in Ukrainian (`ingest-care.ps1`, idempotent; the Ukrainian words and templates are in `data/care-texts-uk.json`, because the embeddings do not bridge languages), so every assistant can cite them |

The compose files of each project are used as they are, plus one small file each in this folder (`orderflow.yml`, `pulse.yml`, `greenhaus.yml`) that points them at the shared broker and seeds the shop. Orderflow and Pulse belong to other repositories, so the script builds them from a clean export of their `main` branch under `../.ecosystem`, never from a working tree that may hold unfinished work.

## What the check proves

Each step asserts something another project must have done: the order's customer is the same Keycloak subject the Greenhaus API sees, billing's grant reaches that subject as a Gardener tier, fulfilment's `device.allocated.v1` makes Pulse register the device with the kit's channels and actuators, the shop's `order.shipped.v1` makes fulfilment ship it in Pulse, the pot's target becomes an alert rule in Pulse and a reading that breaks it raises an alert that reaches the person as a notification, the emulated kit connects to EMQX with the credential from the packing bundle (a wrong password is refused), the pump on auto makes the control loop decide to water, the controller executes the command, Pulse and the audit log say completed, the soil recovers above the target and the loop stops, and a manual run right after is refused by the controller itself (its minimum interval), the claim binds the device to the person in Pulse and links it to a pot, a reading sent to Pulse shows in the pot's status, the assistant (Docs-Brain, mock AI) answers a question about the pot from the facts Greenhaus gave it, including the reading, and the question counts against the monthly quota, and the claim event marks the order as in use in the shop.

## Known limits

- A returned order cannot be un-returned: the contract has no event for undoing a return, so an at-risk grant is only ever revoked.
- The controller is an emulation (`tools/KitEmulator`, a small plant model), not hardware.
- It is a manual check, not part of CI: it needs three repositories and about ten containers.
- Passwords, the client secret and the realm are development data.
- Both Orderflow and Pulse use their own Keycloak on port 8180 when run alone, so they cannot run side by side that way. Here neither starts it.
