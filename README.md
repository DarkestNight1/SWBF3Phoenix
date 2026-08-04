# SWBF3 Phoenix Project

This project is a fork of [SWBF2 Phoenix](https://github.com/Ben1138/SWBF2Phoenix) — a re-implementation of the old Star Wars Battlefront II (2005) game, utilizing the Unity game engine.<br/>
It does so by loading all assets and scripts from the original game files at runtime, providing a compatible API layer for the original, compiled Lua scripts.<br/>
<br/>
This project aims for full compatibility with the vanilla game files, and as best as possible with custom maps.<br/>
<br/>
On top of that, this fork adds the **BF3 Legacy** feature set — recreating Free Radical's cancelled Battlefront III / Elite Squadron on the Phoenix runtime: capital ship destruction, ground-to-space "Vertical Battlefront" layers, lightsaber dismemberment, modernized squad AI with difficulty tiers, modern HDRP lighting + 4K graphics, extended mod support (including detection of the community [Battlefront III Legacy mod](https://www.moddb.com/mods/star-wars-battlefront-iii-legacy)), and data-driven greybox recreations of documented BF3 maps (Coruscant, Cato Neimoidia, Dantooine, Bespin, Desolation Station, Tatooine). See [docs/BF3Legacy.md](docs/BF3Legacy.md).<br/>
<br/>
*Click image to view Video*<br/>
[![Star Wars Battlefront II (2005) Unity Runtime - Update - Tech Demo](https://img.youtube.com/vi/hjSlM5hEfGk/0.jpg)](https://www.youtube.com/watch?v=hjSlM5hEfGk)
<br/>

# How to Build

> **Quick reference:** [INSTALL.md](INSTALL.md) covers running the game and
> installing mods once you have a build. The steps below are the one-time
> build from source, which needs Unity and a C++ toolchain.

## Windows
### Installation Requirements
* Git. Must be either included in PATH or installed with Git Bash.
* Visual Studio 2022 with the following:
    * Workloads:
        * .NET desktop development
        * Desktop development with C++
    * Components:
        * .NET Framework 4.5 targeting pack
        * .NET SDK (should be selected by default)
        * MSBuild (should be selected by default)
* CMake 3.16 or above. Must be included in PATH!
* Unity 2020.3.x with *Windows Build Support (IL2CPP)*

### Build steps
1. Clone **this fork** with submodules:
   `git clone --recurse-submodules https://github.com/DarkestNight1/SWBF3Phoenix`
   If you forgot the submodules, run `git submodule update --init --recursive` afterwards (the build fails without them).
2. Execute `BuildAndCopyLibsWin.bat` (double click)
3. Choose your build type. For now, Debug is recommended
4. Choose the number of threads used for compilation. Recommended is the number of your CPU cores.
5. Wait for the batch to complete. There should be no red text outputs! Yellow is ok. 
6. Three files should've been successfully copied to `UnityProject/Assets/Lib`:
    * `LibSWBF2.dll`
    * `LibSWBF2.NET.dll`
    * `lua50-swbf2-x64.dll`
7. Add the `UnityProject` directory to UnityHub and open it. This might take a while.
8. Open the package manager in *Windows -> Package Manager* and select the "High Definition RP" package. On the right side, expand "Samples" and import "Particle System Shader Samples"
9. Navigate to `Runtime/Scenes` and open PhxMainScene
10. In the hierarchy, select *Game* and set in the inspector:
    * `Mission List Path` to empty!
    * `Game Path String` is **optional** in this fork — the game auto-detects
      Battlefront II in the usual Steam/GOG/retail locations, and shows an
      in-game setup panel if it can't. Set it only to override that. E.g.:
      `C:\Program Files (x86)\Steam\steamapps\common\Star Wars Battlefront II`
11. Go to *File -> Build Settings*, select *PC, Max & Linux Standalone* and choose `Windows` as Target Platform and `x86_64` as Architecture.
12. Click *Build and Run* and choose the `BUILD` directory, residing in the root of this repository

## Linux
### Installation Requirements
* Install via your respective package manager (e.g. `pacman` or `apt`):
    `git gcc make cmake mono msbuild`
* Unity 2020.3.x with *Linux Build Support (IL2CPP)*

### Build steps
1. Clone **this fork** with submodules:
   `git clone --recurse-submodules https://github.com/DarkestNight1/SWBF3Phoenix`
   If you forgot the submodules, run `git submodule update --init --recursive` afterwards (the build fails without them).
2. [ARCH USERS ONLY] If you're on an Arch based system, the current mono package is not correctly installed, which will cause to `LibSWBF2.NET.dll` to not build. Run `arch_mono_4.5_fix.sh` to fix that issue
3. Execute `BuildAndCopyLibsUnix.sh` in your terminal
4. Choose your build type. For now, Debug is recommended
5. Choose the number of threads used for compilation. Recommended is the number of your CPU cores.
6. Wait for the batch to complete. There should be no red text outputs! Yellow is ok. 
7. Three files should've been successfully copied to `UnityProject/Assets/Lib`:
    * `libSWBF2.so`
    * `LibSWBF2.NET.dll`
    * `liblua50-swbf2-x64.so`
8. Add the `UnityProject` directory to UnityHub and open it. This might take a while.
9. Open the package manager in *Windows -> Package Manager* and select the "High Definition RP" package. On the right side, expand "Samples" and import "Particle System Shader Samples"
10. Navigate to `Runtime/Scenes` and open PhxMainScene
11. In the hierarchy, select *Game* and set in the inspector:
    * `Mission List Path` to empty!
    * `Game Path String` is **optional** in this fork — the game auto-detects
      Battlefront II in the usual Steam/GOG locations, and shows an in-game
      setup panel if it can't. Set it only to override that.
12. Go to *File -> Build Settings*, select *PC, Max & Linux Standalone* and choose `Linux` as Target Platform and `x86_64` as Architecture.
13. Click *Build and Run* and choose the `BUILD` directory, residing in the root of this repository


## Known problems
The Terrain has no shader and just appears in purple.
1. In Unity, navigate to `LVLImport/LVLImport/ConversionAssets` and open SWBFTerrainHDRP in ShaderGraph
2. Select the *BlendTerrainLayers* shader node and check whether *Source* is set to `BlendTerrainLayers`. If it's `None`, drag and drop `BlendTerrainLayers.hlsl` (resides right next to `SWBFTerrainHDRP.shadergraph`) into it.
3. Navigate deeper into `LVLImport/LVLImport/ConversionAssets/Resources` and select `HDRPTerrain`
4. Make sure its shader is set to: `Shader Graphs/SWBFTerrainHDRP`


# Legal Notice

Please note that this re-implementation is neither developed by, nor endorsed by LucasArts, Lucasfilm Games or its parent company Disney.

This project does not distribute any original game files, neither full nor partial, and does not include any other Assets that might belong to the trade mark "Star Wars" in any way.
