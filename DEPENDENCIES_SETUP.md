# Missing Dependencies Setup Guide

## Overview
The GTA11Y project requires several external DLLs that are not included in the repository. You need to obtain and place these files manually.

## Required DLLs

### 1. ScriptHookVDotNet3.dll
**What it is:** The ScriptHook V .NET wrapper that allows C# mods to interact with GTA V  
**Where to get it:** https://github.com/crosire/scripthookvdotnet/releases  
**Where to put it:** 
- Primary: `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V\ScriptHookVDotNet3.dll`
- This is where GTA V will load it from at runtime
- The build script references it from this location

**Installation:**
1. Download the latest ScriptHookVDotNet v3 release
2. Extract ScriptHookVDotNet3.dll to your GTA V installation directory
3. Also extract ScriptHookV.dll and NativeTrainer.asi to the same directory

### 2. Tolk.dll
**What it is:** Screen reader abstraction library for accessibility (supports NVDA, JAWS, etc.)  
**Where to get it:** https://github.com/dkager/tolk OR https://www.nuget.org/packages/Tolk/  
**Where to put it:** `C:\dev\gta11y\lib\Tolk.dll`

**Installation:**
1. Download Tolk from GitHub releases or NuGet
2. Create a `lib` folder in the project root: `C:\dev\gta11y\lib\`
3. Copy Tolk.dll into this folder
4. You may also need Tolk's x86 and x64 subdirectories with NVDA/SAPI DLLs

### 3. CSCore.dll
**Status:** ✅ Already in packages folder  
**Action:** Already added to .csproj - no action needed

### 4. NAudio.dll
**Status:** ✅ Already in packages folder and referenced  
**Action:** None

### 5. Newtonsoft.Json.dll  
**Status:** ✅ Already in packages folder and referenced  
**Action:** None

## Directory Structure After Setup

```
C:\dev\gta11y\
├── lib\
│   └── Tolk.dll                    <- YOU NEED TO ADD THIS
├── packages\
│   ├── CSCore.1.2.1.2\             <- Already exists
│   ├── NAudio.1.10.0\              <- Already exists
│   └── Newtonsoft.Json.12.0.3\    <- Already exists
└── GTA\
    └── GTA11Y.csproj

C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V\
├── ScriptHookV.dll                 <- YOU NEED TO ADD THIS
├── ScriptHookVDotNet3.dll          <- YOU NEED TO ADD THIS  
└── scripts\
    └── (your mod will be deployed here)
```

## Quick Setup Steps

1. **Create the lib folder:**
   ```
   mkdir C:\dev\gta11y\lib
   ```

2. **Download and install ScriptHookVDotNet:**
   - Get from: https://github.com/crosire/scripthookvdotnet/releases
   - Extract to GTA V directory

3. **Download and install Tolk:**
   - Get from: https://github.com/dkager/tolk/releases
   - Extract Tolk.dll to `C:\dev\gta11y\lib\`

4. **Run the build script:**
   ```
   build-and-deploy.bat
   ```

## Troubleshooting

### "Could not find ScriptHookVDotNet3.dll"
- Verify GTA V installation path in .csproj matches your actual installation
- Default: `C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V\`
- If different, update line 61 in GTA\GTA11Y.csproj

### "Could not find Tolk.dll"  
- Ensure you created the `lib` folder
- Ensure Tolk.dll is in `C:\dev\gta11y\lib\Tolk.dll`
- Check that you downloaded the correct architecture (should be AnyCPU or have both x86/x64)

### Build succeeds but mod doesn't load in-game
- Make sure ScriptHookV.dll is in GTA V directory
- Make sure .NET Framework 4.8 is installed
- Check scripthookv.log in GTA V directory for errors

## Current Status

After I've made these changes:
- ✅ Added CSCore reference to .csproj
- ✅ Updated ScriptHookVDotNet3 path to Steam location
- ✅ Added Tolk reference to .csproj (pointing to lib folder)
- ✅ Updated build script with NuGet restore and logging
- ❌ **YOU STILL NEED:** Download and place ScriptHookVDotNet3.dll
- ❌ **YOU STILL NEED:** Download and place Tolk.dll

## Next Steps

1. Follow the Quick Setup Steps above
2. Run `build-and-deploy.bat`
3. Check `build_results.txt` for any errors
4. If build succeeds, launch GTA V and test the mod!
