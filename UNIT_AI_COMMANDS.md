# Nuclear Option — Unit AI Command Reference

Notes from decompiling `Assembly-CSharp.dll` (via `ilspycmd`) to understand how to issue immediate orders to spawned units from a BepInEx mod. Scope: `Ship`, `GroundVehicle`, and `Missile` — the unit types relevant to this mod (Buildings are stationary and have no command system).

## Core system

- **`ICommandable`** interface (implemented by `Ship`, `GroundVehicle`, `Missile`):
  ```csharp
  public interface ICommandable
  {
      UnitCommand UnitCommand { get; }
      bool Disabled { get; }
      FactionHQ HQ { get; }
  }
  ```
- **`UnitCommand`** is a `Mirage.NetworkBehaviour` component attached to every commandable unit. It holds a single `Command` (a `GlobalPosition` destination + the issuing `Player` + a timestamp) and exposes:
  ```csharp
  public void SetDestination(GlobalPosition waypoint, bool playerCommand);
  public Command GetCommandCached();
  public event ProcessCommand ProcessSetDestination; // fired when a new destination is set
  ```
- Access it via the unit's public property: `groundVehicle.UnitCommand`, `ship.UnitCommand`, `missile.UnitCommand`.

## How to make a unit act immediately

`SetDestination` branches on network role:
- **If called while running as the server** (`NetworkManagerNuclearOption.i.Server.Active == true`): it calls `ServerSetDestination` **directly and synchronously** — the destination is applied immediately, no network round-trip.
- **If called on a client**: it sends a `CmdSetDestination` ServerRpc and waits for the server to process it (not immediate from the client's perspective).

Since this mod already requires host/server (`BeginPlacementSelection` checks `NetworkManagerNuclearOption.i.Server.Active`), any code running in this mod can call `SetDestination` and have it take effect the same frame:

```csharp
if (unit is GroundVehicle vehicle)
{
    vehicle.UnitCommand.SetDestination(targetPosition, playerCommand: true);
}
else if (unit is Ship ship)
{
    ship.UnitCommand.SetDestination(targetPosition, playerCommand: true);
}
```

`playerCommand: true` attributes the order to the local player (via `GameManager.GetLocalPlayer`); pass `false` for mod/AI-driven orders that shouldn't be attributed to a specific player.

## Available commands per unit type

### `GroundVehicle` (`GroundVehicle : Unit, ICommandable`)
| Method | Purpose |
|---|---|
| `UnitCommand.SetDestination(GlobalPosition, bool playerCommand)` | Immediately move to a waypoint (server) |
| `SetHoldPosition(bool hold)` | Toggle autonomous movement/AI on/off — already used by this mod after spawning a vehicle |
| `bool GetHoldPosition()` | Read current hold-position state |
| `GlobalPosition GetDestination()` | Read current destination |
| `void MoveFromDepot()` | Convenience: sets destination to 60m ahead of current facing (used when a vehicle spawns from a depot) |
| `void StopImmediately()` | Clears pathfinder destination, stops in place |
| `void ReturnToRoad(GlobalPosition? newDestination)` | Navigate back onto the road network, optionally continuing to a destination afterward |
| `void GetMissionWaypoints(SavedVehicle)` | Loads a mission-authored waypoint list and issues the first `SetDestination` |

### `Ship` (`Ship : Unit, ICommandable`)
| Method | Purpose |
|---|---|
| `UnitCommand.SetDestination(GlobalPosition, bool playerCommand)` | Immediately move to a waypoint (server) |
| `SetHoldPosition(bool enabled)` | Toggle hold-position / autonomous sailing — already used by this mod (`holdPosition: false` param on `SpawnShip`) |
| `void GetMissionWaypoints(SavedShip)` | Loads a mission-authored waypoint list |

### `Missile` (`Missile : ..., ICommandable`)
| Method | Purpose |
|---|---|
| `UnitCommand.SetDestination(GlobalPosition, bool playerCommand)` | Same generic destination command |
| `void SetTarget(Unit target)` / `SetTarget(Transform, Rigidbody)` | Retarget the missile mid-flight |
| `void SetAimpoint(GlobalPosition aimPoint, Vector3 targetVel)` | Set/adjust impact aimpoint |
| `void SetProxyFuse(Transform, Rigidbody)` | Configure proximity fuse target |
| `void SetTorque(float torque, float maxTurnRate)` / `SetThrottle(float)` | Low-level flight control |

## Notes / caveats

- `Building`/`BuildingDefinition`-based units have **no** `ICommandable`/`UnitCommand` — they're stationary, so this doesn't apply to structures placed by this mod.
- `SetDestination` throws no error if the unit is `Disabled` — it just silently no-ops (`if (target.Disabled) return;`), so check `unit.Disabled` first if you need to know whether the order was accepted.
- Multiplayer/non-host clients calling `SetDestination` go through a rate-limited `ServerRpc` (`RateLimit(Refill = 5, MaxTokens = 20, Penalty = 1)`) — irrelevant for this mod today since it's host-only, but relevant if the mod is ever extended to support non-host clients issuing orders.
- This reference only covers the generic move/retarget command surface found via decompilation; there is no separate "attack" or "patrol" order struct — combat/engagement behavior is autonomous AI reacting to `SetDestination`/`SetHoldPosition` state, not a distinct command type.
