# Source: Zero Company Mod Studio and Level Editor 2.0.1

The source of the two programs in the 2.0.1 download, by **Clonetrooper163**.

**This is a snapshot, not a working repository.** It holds what the release was built from and is replaced
when the next release goes out. To build it, read [BUILDING.md](BUILDING.md).

| Folder | What it is |
|---|---|
| `ZeroCompanyModStudio.Avalonia/` | The Mod Studio. The program is built as `ZeroCompanyModStudio.exe`. |
| `ZeroCompanyLevelEditor/` | The Level Editor, built as `ZeroCompanyLevelEditor.exe`. |
| `UEToolkit.Core/` | The library both are built on: the mod project model, build, verify and authoring code, and the readers for the game's files. |
| `UEToolkit.App/Rendering/` | The viewport drawing code (OpenGL through Silk.NET) that both programs compile. Only this folder is here, not the application around it. |
| `ZeroCompanyModStudio/` | The few files the Studio's project links: the icon, the two guides and the profile template. |
| `profiles/` | The shipped game profile, with every path and key blanked. |
| `BaseMods/` | The one mod the Studio ships inside itself: `CustomWeaponClasses.json`, which a weapon on a custom Armoury shelf needs. The Studio's build copies it beside the program. |
| `patches/` | Our changes to [CUE4Parse](https://github.com/FabianFG/CUE4Parse), which the build applies to a fresh copy of it. `patches/README.md` says what each one fixes. |
| `ZeroCompanyModStudio.slnx` | The solution that builds all of it. |

[`LICENSE`](LICENSE) is the GNU General Public License v3.0, which covers this project's own code from
version 1.6.0 onward. [`NOTICE.md`](NOTICE.md) lists the third-party components a build carries and the terms
each one comes under.
