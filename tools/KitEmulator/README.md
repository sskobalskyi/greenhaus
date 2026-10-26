# Kit emulator

Pot controllers without the hardware. Each emulated device talks to the MQTT broker the way a real Greenhaus controller will (the topics and payloads of [`device-mqtt.yaml`](../../contracts/asyncapi/device-mqtt.yaml)), so the platform, the automation loop and the charts can be shown working without a soldering iron.

```powershell
# a controller of the pot kit with dry soil, a day cycle that runs 60 times faster than real time
dotnet run --project tools/KitEmulator -- --broker localhost:1884 --device ghx-demo0001:GH-POT-KIT-01 --initial soil=22 --speed 60

# or flashed with the exact devices of an order (the packing bundle of the fulfilment service)
dotnet run --project tools/KitEmulator -- --broker localhost:1884 --provision-from http://localhost:8093/api/fulfilment/v1 --order <orderId> --token <admin bearer>
```

`--help` lists the options. Any kit of [`data/kit-capabilities.json`](../../data/kit-capabilities.json) can be emulated: the starter module, the pot kit and the greenhouse kit.

## What it does

- **Says what it is.** On connect it publishes its manifest (retained, with the hard limits of every actuator), `online` on the status topic (retained) and its state, and it sets a last will that says `offline`. A persistent session means a command sent while it was away waits for it.
- **Reports.** A reading per channel at `--interval`, with a growing `seq`, and the state of every actuator whenever it changes.
- **Obeys, within its limits.** `run_actuator`, `stop_actuator` and `set_kill_switch` are answered with `accepted` and then `completed`, or `rejected`/`failed` with a reason from the contract. The controller is the last line of defence and checks everything itself: the maximum run of a command, the minimum interval and the daily cap, the thermal cutoff of the heater (it trips at 35 degrees and the run fails, whatever was asked), an empty tank, a pump that does not wet the soil (blocked after three fruitless runs until the kill switch is switched off), the kill switch. A command past its expiry never runs, and a command it has already seen is answered again and not run again (QoS 1 may deliver it twice).
- **Has a plant to look after.** A small deterministic model: the soil dries (faster when warm), a running pump wets it in proportion to the seconds it runs and empties the tank, the heater and the fan move the temperature, the lamp adds light to a day cycle, the humidifier and the fan move the air. `--speed` makes the surroundings change faster for a demo, while what an actuator does stays tied to the seconds it really runs. The charts then show a real loop: dry, pump, recovery.

## Tests

`dotnet test tests/Greenhaus.KitEmulator.Tests`: the physics and the controller on a fake clock (limits, expiry, repeats, kill switch, cutoff, empty tank, fruitless runs, state on change), and the emulator against a real EMQX in a container, every message checked against the AsyncAPI document.

Not done: per-device broker credentials work from the emulator's side (`--credential`), but until Pulse verifies them the local broker is anonymous.