# 10. Notifications

Status: accepted

## Context
Monitoring, alerts and notifications are free for everyone (spec R1), and a person must hear about what matters without watching the app: an alert on a pot, a controller going offline, a care task that is due, a plan that changed or is at risk, an actuator the automation had to stop. The producers are events from other projects and jobs inside the API.

## Decision
- A notification stores only a **kind** and its **params** (strings), never text. The API sends them as they are and the clients translate them in English and Ukrainian, as they already do for problem codes. A kind is one of: alert_raised, alert_resolved, device_offline, device_online, actuator_blocked, care_task_due, plan_changed, plan_at_risk, automation_suggestion. The wording can change without touching stored data.
- The contract has `GET /notifications` (newest first, `unread`, `limit`), `POST /notifications/{id}/read` (idempotent, someone else's is a 404) and `POST /notifications/read-all`. The mock implements them with seeded samples.
- `IUserNotifier.NotifyAsync(userId, potId, kind, params)` is the one way code inside the API tells a person something; `UserNotifier` also offers a deduplicated variant. A **dedupe key** names the thing told about, with a unique index per user, so redelivered events and repeated checks never double a notification. An optional window lets a thing that keeps happening be told again after a while but never in a burst.
- Producers:
  - `alert.raised.v1` / `alert.resolved.v1` from Pulse: the device is mapped to its pot and owner, one notification per alert id and state, with the metric, direction and value, and the zone by position (never its label, which is typed text) in the zone view. An alert of a device nobody claimed is ignored.
  - `device.status.v1`: offline and online, each told once per 15 minutes per device, so a controller that keeps dropping does not fill the list.
  - `entitlement.changed.v1`: a lower tier is `plan_changed`, a grant at risk is `plan_at_risk` with the deadline. An event that was not applied (older than the stored one) says nothing.
  - A care task that has come due, by a background job with a configurable interval (`Notifications:CareInterval`), once per task.
- Consumers keep the inbox and dead-letter behaviour of ADR 3, and the new routing keys are bound with the others.
- No push or email delivery in this step: a notification is something the app shows. Email and web push can read the same table later.

## Consequences
- One list for everything that needs attention, scoped to the person, with an unread state.
- Texts live in the clients only, so the API stays free of language.
- The automation loop and other producers need only `IUserNotifier`.
- The unread count is the length of the unread list (up to 100), which is what a bell shows; a separate counter endpoint was not needed.