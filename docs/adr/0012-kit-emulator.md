# 12. A kit emulator for the controllers

Status: accepted

## Context
The automation loop (ADR 11) decides and Pulse delivers commands over MQTT, but nothing executes them without hardware, so the loop cannot be shown closed (dry soil, pump, recovery), and the platform cannot be tested against anything that behaves like a controller. The Pulse simulator is a plain telemetry load generator and stays one.

## Decision
- The emulator lives in `tools/KitEmulator`, in this repository, and speaks only the device contract (`device-mqtt.yaml`) to the broker. It needs nothing from Pulse's code, and Pulse needs nothing from it.
- It is split in three so each part can be tested without the others: `PlantPhysics` (pure, deterministic, takes seconds), `KitController` (the firmware: limits, expiry, repeats, acks, reports, on a `TimeProvider` and an `IMessageSink`) and `MqttDevice` (the connection, a persistent session, reconnect with a growing pause, a last will, a goodbye on stop).
- **The controller enforces the hard limits on its own**, as the contract says: whatever the cloud asks, a run beyond the maximum, inside the minimum interval or over the daily cap is refused, the heater trips at its thermal cutoff, a pump stops on an empty tank and is blocked after fruitless runs. Greenhaus's soft limits are tighter, the device is the last line.
- **Time:** the surroundings (drying, cooling, the day cycle) scale with `--speed`; an actuator's effect is tied to the seconds it really runs, so a 10 second pump run waters the same at any speed. Hard limits count real seconds.
- **At-least-once delivery:** a command id the controller has seen is not run again and the platform gets the same answer again (the last 2000 ids are remembered).
- A command it cannot read is ignored, never an error into the connection. Commands are handled by a worker, not inside the receive callback, because answering means publishing.
- It can be flashed with the devices of an order from the fulfilment packing bundle (`--provision-from`), with the credential, so the platform's broker authentication can be exercised once Pulse checks it.
- Tests run on a fake clock for the logic and against a real EMQX container for the wire, and check every message in both directions against the AsyncAPI document.

## Consequences
- The closed loop can be shown and tested end to end in the ecosystem script.
- The model is deliberately simple. It is for showing the loop, not for predicting a real plant.