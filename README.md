# Building Placement Plugin

A BepInEx plugin for **Nuclear Option** that adds a comprehensive building, vehicle, and ship placement system with proximity-based construction requirements.

## Overview

This plugin provides an in-game UI for placing structures, vehicles, and ships directly without using the standard commander interface. Each unit type has unique placement requirements based on nearby facility proximity.

## Major Features

### 🏗️ Three-Category Placement System

The plugin organizes all placeable units into three categories:

#### **1. Structures (Buildings)**
- Requires proximity to a **Jackknife** construction vehicle
- Distance scaling: 50-120m depending on building size and type
- Buildings are snapped to terrain for proper foundation placement
- Includes HQs, power plants, factories, research labs, defensive structures, and more

#### **2. Naval (Ships)**
- Requires proximity to a **Large Factory** facility
- **Distance constraint: 350-800m from Large Factory** (size-dependent)
- Minimum distance requirement: **350m** (must not be placed too close)
- Placement on water surface with proper sea-level intersection
- Dynamic help text shows distance range based on selected ship size
- Timeout: 5 seconds to find a Large Factory or placement is cancelled with refund

#### **3. Vehicles (Ground Units)**
- Requires proximity to a **Vehicle Depot** facility
- Distance requirement: 75m radius
- Includes tanks, APCs, and other ground vehicles
- Timeout: 5 seconds to find a depot or placement is cancelled with refund

### 💰 Faction Funds Display
- Real-time faction funds shown at the top of the UI
- Instant visual feedback when unit costs are deducted
- Auto-refund if placement timeout occurs

### 🎨 Enhanced User Interface
- Collapsible category headers with expand/collapse toggles
- Dynamic help text showing unit-specific placement requirements
- Clear status messages for placement success/failure
- Build time multiplier controls for customizing construction speed
- Settings button for build time adjustments
- Large window (470px wide, 800px tall) with scrollable unit list

### ⏱️ Build Queueing System
- Queue multiple placements in sequence
- Configurable build time multipliers
- Visual countdown and progress tracking
- Real-time status updates on placement progress

## Installation

1. Ensure you have **BepInEx 5.x** installed for Nuclear Option
2. Copy `BuildingPlacement.dll` to:
   ```
   Nuclear Option\BepInEx\plugins\
   ```
3. Copy `BuildingPlacement.pdb` to the same folder (optional, for debugging)
4. Launch Nuclear Option — the plugin will load automatically

## Usage

### Opening the Plugin

- Press the Building Placement hotkey (configurable in-game) to open the main UI window
- The window appears as "BUILDING PLACEMENT" at the top

### Placing Units

1. **Select a Unit**: Click on any building, vehicle, or ship in the scrollable list
2. **View Requirements**: The help text updates to show placement rules for your selected unit
3. **Click "SELECT LOCATION"**: Button changes state to "CANCEL LOCATION SELECTION"
4. **Place on Map**: Click on the tactical map or 3D world to attempt placement
5. **Wait for Facility**: The system searches for the required nearby facility (Jackknife, Large Factory, or Vehicle Depot)
6. **Confirm Orientation**: Use prompts to orient the unit if needed
7. **Watch Build Progress**: Status messages show remaining build time

### Build Time Controls

- Click the **DBG (Build Time Controls)** button to open build time adjustment panel
- Use **-** and **+** buttons to adjust the multiplier for new queued placements
- Current multiplier shows in the main UI

### Cancelling Placement

- Click "CANCEL LOCATION SELECTION" to abort an active placement attempt
- No refund is given for completed placements, but cancelled attempts return all funds

## Technical Details

### Facility Detection

- **Jackknife**: Detected by matching unit type names against normalized patterns (case-insensitive)
- **Large Factory**: Detected by Encyclopedia ship unit definitions and building asset names
- **Vehicle Depot**: Detected using VehicleDepot component inspection

### Placement Mechanics

- **Structures**: Raycast from camera through world position, snap to terrain
- **Ships**: Ray-plane intersection at sea level (Datum.LocalSeaY) for camera-independent placement
- **Vehicles**: Standard terrain raycast with snapping

### Distance Validation

Ships enforce proximity constraints:
- **Minimum distance**: 350m from Large Factory (prevents placement too close)
- **Maximum distance**: 350-800m (size-dependent scaling)
- Formula: `350f + ((shipSize - 20f) / 30f) * 450f`, clamped to [350, 800]

Smaller ships default to 350m max, larger ships scale up to 800m based on their dimensions.

### Build System

- All placements queue and build in sequence
- Configurable build delay (default varies by unit type)
- Timeout for facility searches: 5 seconds (ships and vehicles)
- Auto-refund on timeout failure
- Preview object shows placement location during build countdown

## Configuration

Build time multipliers can be adjusted in-game via the Settings panel. No external config files required for basic usage.

## Requirements

- **Nuclear Option** game
- **BepInEx 5.x** framework
- **.NET Framework 4.7.2** (game requirement)

## Troubleshooting

### "No Large Factory found"
- Ensure a Large Factory is within the required 350-800m range
- Check that the facility is built and active
- Verify you're placing the ship within 5 seconds

### Ship placement fails with distance error
- "Ship placed too close" = within 350m of factory (move farther away)
- Beyond max range = outside 800m distance (move closer to factory)

### Building won't place near Jackknife
- Ensure Jackknife is within range (50-120m depending on building type)
- Try placing in a more open area
- Verify unit is selected and not already cancelled

## Development

This is a .NET Framework 4.7.2 project targeting the Nuclear Option BepInEx environment.

### Building from Source

```powershell
dotnet build -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option" -c Release
```

Output: `bin\Release\net472\BuildingPlacement.dll`

### Project Structure

- `CommanderBuildingPlacementService.cs` - Core placement logic and facility detection
- `CommanderBuildingPlacementUi.cs` - Main UI window rendering
- `CommanderBuildingPlacementController.cs` - Update loop and event handling
- `CommanderBuildingPlacementMapService.cs` - Tactical map integration
- Support files for camera, input, UI theming, and settings

## Credits

Thanks to **rose.clara** for the **NOCommander** mod, which this Building Placement plugin is heavily based upon. The NOCommander mod provided the foundation for unit placement mechanics, UI systems, and integration with the Nuclear Option game engine.

## License

See LICENSE file in project root.

## Version

Current version: **1.0.0** (last updated August 15, 2026)

---

For bug reports or feature suggestions, refer to the project repository or contact the developer.
