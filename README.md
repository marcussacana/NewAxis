# NewAxis 3D Manager

**NewAxis** is a free and open-source launcher and mod manager that brings **3D gaming** to LeiaSR-compatible monitors—without being locked into proprietary hardware!

## Supported Monitors

NewAxis **should** works with any **LeiaSR-compatible monitor**, including:

- **Samsung Odyssey 3D** ✅ (Tested)
- **Acer SpatialLabs**
- **Sony Spatial Reality Display**
- **Lenovo ThinkVision**
- And other LeiaSR-compatible displays

## Features

- **Easy Game Management** - Browse and select your installed games
- **One-Click Mod Installation** - Install 3D mods with a single click
- **Multi-Language Support** - Available in English, Portuguese, Spanish, and Japanese
- **Customizable Settings** - Adjust depth, popout, and hotkeys to your preference
- **Automatic Updates** - Stay up-to-date with the latest features
- **AMD Support** - NewAxis automatically apply fixes for AMD GPUs

## Getting Started

### Installation

1. **Download** the latest release from the [Releases page](../../releases)
2. **Extract** the files to a folder of your choice
3. **Run** `NewAxis.exe`

### First Use

1. **Launch NewAxis** - The application will automatically detect supported games
2. **Select a Game** - Click on a game from the sidebar
3. **Browse for Game Path** - If not auto-detected, click the folder icon to locate your game
4. **Choose a Mod** - Use the dropdown next to the "Start" button to select:
   - **3D Ultra** - Full 3D experience with shader mods
   - **3D+** - Enhanced 3D with Reshade effects
5. **Adjust Settings** - Fine-tune depth and popout values using the sliders
6. **Click Start** - The mod will be installed and your game will launch!

### Settings

Click the gear icon in the sidebar to access:

- **Language** - Choose your preferred language
- **Install mod temporarily** - Automatically removes mods after closing the game
- **Disable DLSS** - Disable DLSS for better 3D compatibility
- **Hotkeys** - Customize in-game shortcuts for depth and popout adjustments


## Contributing Community Mods

Want to add support for a new game? We welcome community contributions!

### How to Submit a Mod

1. **Fork** The NewAxisData repository
2. **Add your mod files** to the repository
3. **Create a `community.json`** file following the same structure as `index.json`
4. **Submit a Pull Request** with:
    - Mod binary files (`.7z` archives)
    - Metadata in `community.json`
    - Game name, creator info, and download URLs+
    - Follow the structure of the `index.json` file


## Roadmap
1. Make current mods work  
2. Add Lenovo games
3. Add Samsung games
4. Add geo-11 community fixes

Notes:
- NewAxis will withhold patch updates for a minimum of three months after a new game is released on Acer, Samsung, or Lenovo platforms.
- If a currently released game crashes due to a missing update, it may be eligible for an early update before the three-month period ends.
- Games with native support (such as Tomb Raider on Acer or Stellar Blade on Samsung) will likely not be supported by NewAxis.

## Requirements

- **Windows** 10/11
- **LeiaSR-compatible monitor**
- Supported games installed on your PC

## Troubleshooting

### Game Not Detected
- Click the folder icon next to "Installed at:" and manually browse to your game folder
- Make sure the game is installed in a standard location (Steam, Epic Games, etc.)

### Mod Not Working
- Verify your monitor is LeiaSR-compatible
- Try disabling DLSS in settings
- Make sure you're using the correct mod type for your game

### Application Won't Start
- Star the program with CMD line `NewAxis.exe > output.log`
- Check the output.log file for errors
- Run as Administrator if you encounter permission issues

## License

This project is open-source and available under the MIT License.

## Credits

**NewAxis Project** - By Marcussacana

Special thanks to the 3D gaming community and all contributors who help expand game support!

## Disclaimer:

This project is independent and is not affiliated with, endorsed by, sponsored by, or otherwise associated with Acer Inc., Samsung, or any other company. All trademarks belong to their respective owners.

---

**Enjoy your 3D gaming experience!**

For issues, suggestions, or questions, please visit the [Issues page](../../issues).
