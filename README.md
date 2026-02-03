# GTA11Y - Grand Theft Accessibility

An accessibility mod for Grand Theft Auto V designed to make the game playable for blind and visually impaired gamers.

## What is it?

GTA11Y started as an attempt to create accessibility functions for GTA V. The original author placed the source code here for the community to tinker with. In January 2026, significant development work was done to expand the mod with comprehensive navigation, combat, and environmental awareness features.

## Features

### Navigation Assist System
The core of GTA11Y is a multi-directional obstacle detection system that works both on foot and in vehicles.

- **360-Degree Detection** - Scans for obstacles in four zones: left, center, right, and behind (behind only when reversing)
- **Hybrid Detection** - Uses proximity scanning for peds and vehicles, raycasts for world geometry like walls and buildings
- **Speed-Adaptive** - Detection range and check frequency scale with vehicle speed for safer driving
- **Stereo Audio Panning** - Obstacles play through left/right speakers based on their position
- **Entity Differentiation** - Different audio tones for pedestrians, vehicles, and walls so you know what you're approaching
- **Behind Zone** - When moving backwards, obstacles behind you play at center pan with an octave-lower frequency

### Waypoint & Mission Tracking
- **Waypoint Tracking** - Press NumPad Decimal to start tracking your map waypoint with directional beeps
- **Mission Marker Tracking** - Automatically finds and tracks mission blips, strangers and freaks, and other objectives
- **Distance-Based Beeps** - Beeps get faster as you approach your destination
- **Directional Panning** - Audio pans left/right to guide you toward your target
- **Double-Tap Toggle** - Single tap NumPad Decimal announces your heading, double-tap toggles tracking on/off

### Turn-by-Turn Navigation
When tracking a waypoint or mission marker while driving:
- Announces turns: "Turn left in 50 meters", "Bear right", "Turn around"
- Adapts announcement timing based on vehicle speed
- Works with both waypoint and mission marker tracking

### Enemy Detection
- **Automatic Scanning** - Detects hostile peds within 100 meters every 2 seconds
- **Combat Alerts** - Announces how many hostiles are detected
- **Directional Beeps** - Plays harsh sawtooth tones panned toward enemy positions
- **Behind Support** - Enemies behind you play at center pan with lower frequency

### Combat Feedback
- **Hit Confirmation** - Plays hit.wav when your shots connect
- **Headshot Indicator** - Plays headshot.wav for headshots
- **Kill Confirmation** - Plays kill.wav when an enemy is eliminated
- **Ammo Announcement** - Automatically announces weapon name, magazine count, and total ammo when switching weapons
- **Low Ammo Warning** - Spoken warning when magazine drops below 25%

### Vehicle Awareness
- **Entry Announcement** - Announces vehicle name when entering ("Entering Albany Emperor")
- **Passenger Count** - Scans nearby vehicles and announces occupant count
- **Vehicle Health Feedback** - Spoken warnings when body, engine, or fuel tank health drops to 75%, 50%, 25%, and 10%
- **Traffic Awareness** - Warns when fast vehicles approach from sides or behind

### Environmental Awareness
- **Water Detection** - Low bass rumble (80-100Hz) warns of water ahead
- **Dropoff Detection** - Descending sine tone indicates ledges and long drops
- **Door Detection** - Plays door.wav with directional panning when doors are nearby
- **Ladder Detection** - Plays ladder.wav with directional panning for climbable surfaces
- **Indoor/Outdoor Detection** - Announces when you enter or exit building interiors
- **Swimming Depth** - Announces depth in meters when swimming underwater
- **Slope Feedback** - Announces steep uphill, downhill, or level ground changes

### Proximity Alerts
- **Pickup Detection** - Detects health, armor, weapons, and money pickups within range
- **Safe House Proximity** - Alerts when near your character's safe house (character-specific)
- **Service Proximity** - Announces nearby Ammu-Nations, hospitals, clothing stores, mod shops, barbers, tattoo parlors, and ATMs
- **Interactable Detection** - Alerts for stores, mission givers, and other interactable locations
- **Cover Detection** - During combat, indicates nearby cover positions with directional audio

### Wanted Level
- **Level Changes** - Announces wanted level changes
- **Wanted Details** - When enabled, periodically announces cop count and helicopter presence

### Information Commands
- **NumPad 0** - Announces current location, street, zone, and cash
- **Ctrl+NumPad 0** - Announces current in-game date and time
- **NumPad 4** - Scan nearby vehicles with distance and direction
- **NumPad 5** - Scan nearby doors and props
- **NumPad 6** - Scan nearby pedestrians
- **NumPad 8** - Scan nearby objects
- **NumPad Decimal** - Single tap: announce heading. Double tap: toggle waypoint/mission tracking

### Settings Menu (NumPad Enter to open)
Navigate with NumPad 1/3, toggle with NumPad 2:
- Navigation Assist on/off
- Turn-by-Turn Navigation on/off
- Pickup Detection on/off
- Water/Hazard Detection on/off
- Vehicle Health Feedback on/off
- Stamina Feedback on/off
- Traffic Awareness on/off
- Wanted Level Details on/off
- Slope/Terrain Feedback on/off
- Interactable Detection on/off
- Cover Detection on/off
- Detection Radius (10m / 25m / 50m / 100m)
- Plus existing settings: God Mode, Vehicle God Mode, Infinite Ammo, various cheat options

### Other Features
- Teleport to saved locations (Michael's House, Airport, Fort Zancudo, etc.)
- Vehicle spawning menu with all game vehicles
- Altitude indicator (audio pitch based on height)
- Target pitch indicator when aiming
- Heading announcements as you turn
- Zone and street announcements as you travel
- Speed announcements while driving

## Audio Files

Place these .wav files in your scripts folder:
- `pickup.wav` - Pickup/item detection sound
- `cover.wav` - Cover position indicator
- `interact.wav` - Interactable object indicator
- `hit.wav` - Shot hit confirmation
- `headshot.wav` - Headshot indicator
- `kill.wav` - Kill confirmation
- `door.wav` - Door proximity (mono recommended for panning)
- `ladder.wav` - Ladder proximity (mono recommended for panning)
- `tped.wav` - Targeting pedestrian sound
- `tvehicle.wav` - Targeting vehicle sound
- `tprop.wav` - Targeting prop sound
- `hashes.txt` - Vehicle and weapon name lookup table

## Dependencies

- Microsoft Visual Studio 2019 or later
- [NAudio](https://github.com/naudio/NAudio) - Audio playback and signal generation
- [Tolk](https://github.com/dkager/tolk) - Screen reader output
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) - Settings storage
- [Script Hook V .NET](https://github.com/crosire/scripthookvdotnet) - .NET scripting framework
- [Script Hook V](https://www.dev-c.com/gtav/scripthookv/) - Native function access

## Installation

1. Install Script Hook V and Script Hook V .NET to your GTA V directory
2. Build the project or obtain GrandTheftAccessibility.dll
3. Copy GrandTheftAccessibility.dll to your GTA V scripts folder
4. Copy all required .wav files and hashes.txt to the scripts folder
5. Launch GTA V

## Building

1. Open GTA.sln in Visual Studio
2. Restore NuGet packages
3. Build in Release/x64 configuration
4. Output will be in GTA\bin\x64\Release\

## Development History

**Original Development** - Basic accessibility functions including location announcements, weapon detection, teleportation, and vehicle spawning.

**January 2026 Revival** - Comprehensive expansion adding:
- Navigation Assist v3.0 with multi-directional stereo detection
- Waypoint and mission marker tracking systems
- Enemy detection with 3D positioned audio
- 11 "Batch 1" features (pickup detection, hazard warnings, vehicle health, etc.)
- 15 "Batch 2" features (combat feedback, vehicle ID, environmental detection, etc.)
- Settings menu integration for all new features
- Numerous bug fixes and API compatibility updates

## Legal Stuff

This project is provided as-is. No guarantees are made that it will work. Use at your own risk. This mod is intended to make GTA V more accessible and is shared with the community in that spirit.
