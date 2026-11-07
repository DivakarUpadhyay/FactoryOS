# FactoryOS edge agent

The edge agent (`factoryos-edge`) is a small program that runs next to the machines: on a plant PC, an industrial PC or a Linux
gateway. It is the standard OT core of FactoryOS:

- **Reads the machines itself** with the same drivers as the cloud worker: OPC UA, Modbus TCP, MQTT, Sparkplug B, Siemens S7,
  MTConnect, SECS/GEM (HSMS), UR RTDE, ROS 2 (rosbridge) and the simulator.
- **Store and forward**: every reading goes to a disk buffer first (segment files, 512 MB by default). When the network or the cloud
  is down the agent keeps reading; when it is back, the buffer is sent in order, each reading once (a half-sent segment resumes
  where it stopped after a restart). If the buffer fills up, the oldest readings are dropped and counted (shown in FactoryOS).
- **Works offline from the start**: the last configuration is cached on disk, encrypted (AES-GCM) with a key derived from the agent
  key, because it holds device passwords.
- **AI next to the machines**: a streaming detector per analog signal (spikes, slow drift by CUSUM, frozen sensors) sees a problem
  within seconds, even without the cloud. Findings appear under **OT AI insights** with the source `edge:<agent name>`.
- **Unified Namespace (optional)**: publishes each reading, retained, to a local MQTT broker as `factoryos/<asset code>/<tag code>`
  so other OT applications on the plant network can use one consistent tree.

While an agent is switched on, the cloud worker does not poll its assets (no double reads, no double writes).

## Set up

1. In FactoryOS: **Connectivity → Edge agents → Agent**. Give it a name ("Line 1 gateway"). Copy the key; it is shown once.
2. **Assets**: pick the machines this agent reads. Their protocol, address and tags are configured as usual on each asset.
3. Build or copy the program:

   ```
   dotnet publish src/Divakar.FactoryOS.Edge -c Release -r win-x64 --self-contained -o publish/edge-win
   dotnet publish src/Divakar.FactoryOS.Edge -c Release -r linux-x64 --self-contained -o publish/edge-linux
   dotnet publish src/Divakar.FactoryOS.Edge -c Release -r linux-arm64 --self-contained -o publish/edge-arm64
   ```

4. Edit `appsettings.json` next to the program:

   ```json
   "Edge": { "Url": "https://your-factoryos-api", "Key": "fos_edge_…", "DataPath": "data", "BufferMb": 512, "LocalAi": true,
             "UnsBroker": null, "UnsPrefix": "factoryos" }
   ```

   Every setting can also come from an environment variable, e.g. `FACTORYOS_EDGE_Edge__Key`.

5. Run it:
   - console (to try): `factoryos-edge`
   - Windows service: `sc create "Divakar FactoryOS Edge" binPath= "C:\FactoryOS\edge\factoryos-edge.exe" start= auto` then `sc start "Divakar FactoryOS Edge"`
   - Linux (systemd), `/etc/systemd/system/factoryos-edge.service`:

     ```ini
     [Unit]
     Description=Divakar FactoryOS edge agent
     After=network-online.target
     [Service]
     Type=notify
     WorkingDirectory=/opt/factoryos-edge
     ExecStart=/opt/factoryos-edge/factoryos-edge
     Restart=always
     [Install]
     WantedBy=multi-user.target
     ```

     `systemctl enable --now factoryos-edge`

The agent page shows whether it is online, its version and host, how many readings are waiting in its buffer and how many were
dropped, and its last error. **New key** replaces the key (the old one stops working at once); **Switch off** stops it being
accepted and hands its assets back to the cloud worker.

## Network

The agent only makes outgoing HTTPS calls to the FactoryOS API (`/api/v1/edge/config`, `/upload`, `/heartbeat`, `/insights`) with
the header `X-Edge-Key`. No inbound port is opened in the plant. Readings are time-stamped on the agent, so keep its clock right
(NTP); it warns in its log when the clock is more than a minute off the server.

## What is and is not proven

The drivers are tested against protocol-level test servers (HSMS equipment, RTDE controller, Sparkplug payloads, rosbridge
messages) and the OPC UA, Modbus, MQTT and S7 libraries they are built on. They have not yet been run against a customer's
production equipment; commission each machine with the site acceptance checklist below before relying on its data.

## Site acceptance checklist (per machine)

1. Connection: the asset shows **Running/Idle** and *Last data* updates at the poll interval.
2. Values: compare three tags against the machine's own HMI at the same moment (within the tag's deadband and the meter's accuracy).
3. Counters: energy and part counters only rise; reset the counter once on purpose and check the day's total is not inflated.
4. Units and scaling: check scale/offset on each tag against the device manual (e.g. Modbus register in 0.01 kWh).
5. Offline: pull the network cable of the agent for 10 minutes; reconnect; check the history has no gap and *Buffered* returns to 0.
6. Meter register: add the meter with its accuracy class and last calibration certificate (ISO 50001 → Meters & calibration).
