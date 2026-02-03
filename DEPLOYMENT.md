# GTA11Y Deployment Guide

## Prerequisites

Before running the deployment script, make sure you have:

1. **Built the project** in Visual Studio:
   - Configuration: **Release**
   - Platform: **x64**
   - Build → Build Solution (or press F6)

2. **ScriptHookV and ScriptHookVDotNet installed** in your GTA V directory:
   - Download ScriptHookV from: http://www.dev-c.com/gtav/scripthookv/
   - Download ScriptHookVDotNet from: https://github.com/crosire/scripthookvdotnet/releases
   - Extract both to your GTA V root folder

3. **Tolk.dll** in your scripts folder (for screen reader support)

## How to Deploy

1. **Run as Administrator** (required for copying to Program Files):
   - Right-click on `deploy.bat`
   - Select "Run as administrator"

2. The script will:
   - Create the `scripts` folder if needed
   - Copy all compiled DLLs
   - Copy dependency libraries (NAudio, CSCore, Newtonsoft.Json)
   - Copy audio files (tped.wav, tvehicle.wav, tprop.wav)
   - Copy the hashes database (hashes.txt)
   - Launch GTA V via Steam

## Files Deployed

### Main Mod Files
- `GrandTheftAccessibility.dll` - Main mod DLL

### Dependencies
- `NAudio.dll` - Audio playback library
- `CSCore.dll` - Audio processing library
- `Newtonsoft.Json.dll` - JSON settings handling

### Resources
- `tped.wav` - Pedestrian targeting sound
- `tvehicle.wav` - Vehicle targeting sound
- `tprop.wav` - Prop targeting sound
- `hashes.txt` - Object hash database

## Troubleshooting

**"GTA V installation not found"**
- Verify your GTA V is installed at: `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V`
- If it's in a different location, edit the `GAME_DIR` variable in `deploy.bat`

**"Failed to create scripts directory" or copy errors**
- Make sure you're running the script as Administrator
- Check that GTA V isn't currently running

**"Build files not found"**
- Build the project first in Visual Studio (Release x64 configuration)
- Check that files exist in `GTA\obj\x64\Release\`

## Manual Deployment

If the batch script doesn't work, you can manually copy files:

1. Copy from `GTA\obj\x64\Release\`:
   - `GrandTheftAccessibility.dll` → `[GTA V]\scripts\`

2. Copy from `packages\` (libraries):
   - `NAudio.1.10.0\lib\net35\NAudio.dll` → `[GTA V]\scripts\`
   - `CSCore.1.2.1.2\lib\net35-client\CSCore.dll` → `[GTA V]\scripts\`
   - `Newtonsoft.Json.12.0.3\lib\net45\Newtonsoft.Json.dll` → `[GTA V]\scripts\`

3. Copy from `External Resources\`:
   - `tped.wav` → `[GTA V]\scripts\`
   - `tvehicle.wav` → `[GTA V]\scripts\`
   - `tprop.wav` → `[GTA V]\scripts\`
   - `hashes.txt` → `[GTA V]\scripts\`

## Testing

After deployment:
1. Launch GTA V (the script does this automatically)
2. Load into the game
3. You should hear "Mod Ready" when the mod loads
4. Press Ctrl+NumPad2 to toggle accessibility keys on/off
