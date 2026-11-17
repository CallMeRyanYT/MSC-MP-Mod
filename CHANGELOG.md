# Changelog

## 0.9.0 - 2026-11-17

A complete rewrite. Nothing of the old networking code is left.

- **Connecting:** Steam lobbies, invites and P2P instead of direct IP connections, so there are no addresses, ports or VPNs to set up. Version and game-build checks happen when you join.
- **Shared world:** friends get an exact copy of the host's save, plus everything that happened since the host loaded (world log). Their own save is backed up and restored automatically, including after a crash. The host's save is never touched.
- **Player models:** rebuilt from the game's own characters, clothes and animations, with walking, running, crouching, head look, driving and passenger poses and waving. Body and clothes can be chosen in the settings.
- **Items and parts:** pick up, carry, drop and throw with host-arbitrated ownership. Spawned objects are linked between games.
- **Vehicles:** every car, the boat and the trailer. The driver simulates the car; replicas get the same inputs with physics correction. Passenger seats.
- **World interactions:** state-machine mirroring for doors, switches, appliances, the dashboard, the ignition, assembling and removing parts, bolts and wiring, with player-only effects filtered out.
- **Time, weather and money** follow the host; the wallet is shared.
- **Interface:** F2 panel, chat, player list, name tags, notifications.
- **Install:** one-click `Install.bat` that finds the game in any Steam library, checks MSCLoader, removes old builds and installs. Also `Uninstall.bat`.
- **Build safety:** an API audit checks every game API the mod uses against the real game assemblies.

## 0.5.0 and older

Experimental builds made with an AI coding agent (Codex), using direct LiteNetLib connections. They were replaced by 0.9.0 because players couldn't connect.
