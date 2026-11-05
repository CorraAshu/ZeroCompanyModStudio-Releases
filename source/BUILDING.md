# Building from source

This folder is the source of **Zero Company Mod Studio** and **Zero Company Level Editor**, version
**2.0.0**, as it stood when that release was built. It is a snapshot per release, not a working
repository: the next release replaces it, and nothing here is updated in between.

These are the steps this source is checked with before a snapshot is published: they are run, as written,
from an empty folder, and must end in a Release build with no errors. Everything below is PowerShell, run
from the root of this repository unless it says otherwise.

## What you need

- Windows 10 or 11, 64-bit.
- The **.NET 10 SDK** (`dotnet --version` prints 10.x).
- **Git**.
- For CUE4Parse's native library (it decodes the game's compressed animation): **Visual Studio 2022**, or
  its Build Tools, with the *Desktop development with C++* workload, and **CMake 3.10 or newer** on your
  PATH. Without them the managed projects still build -- CUE4Parse's project prints
  `CUE4Parse-Natives build failed. Continuing without it.` and carries on -- but the programs cannot decode
  ACL-compressed animation.
- An internet connection for the first restore: NuGet packages, CUE4Parse and its submodules.

## The folder layout

`UEToolkit.Core` refers to CUE4Parse as `..\..\FModel-dev\CUE4Parse`. From `source\UEToolkit.Core` that is the
root of this repository, so CUE4Parse goes in a `FModel-dev` folder **beside `source`**, not inside it:

```
ZeroCompanyModStudio-Releases\        the root of this repository
    source\                           this folder
        ZeroCompanyModStudio.slnx
        ZeroCompanyModStudio.Avalonia\    the Mod Studio
        ZeroCompanyLevelEditor\           the Level Editor
        UEToolkit.Core\                   the library both are built on
        UEToolkit.App\Rendering\          the drawing code both compile
        BaseMods\                         the custom weapon classes mod the Studio ships inside itself
        patches\                          our changes to CUE4Parse
    FModel-dev\CUE4Parse\             you add this in step 2; the repository's .gitignore leaves it alone
```

None of the project files was changed to make this layout work.

## 1. This repository

```powershell
git clone https://github.com/CorraAshu/ZeroCompanyModStudio-Releases.git
cd ZeroCompanyModStudio-Releases
```

## 2. CUE4Parse, at the commit the patches were written against

```powershell
git clone https://github.com/FabianFG/CUE4Parse.git FModel-dev/CUE4Parse
git -C FModel-dev/CUE4Parse checkout cc79bf646cc688dad9a7677ce2c327c07111f39c
git -C FModel-dev/CUE4Parse submodule update --init CUE4Parse-Natives/ACL/external/acl
git -C FModel-dev/CUE4Parse/CUE4Parse-Natives/ACL/external/acl submodule update --init external/rtm
```

That is one of upstream's own commits, so the clone above is all the patches need.

The last two commands fetch the Animation Compression Library and the Realtime Math library it is built on
(both MIT; see `NOTICE.md`). Nothing else in the submodule tree is needed.

## 3. Apply the patches

```powershell
$patches = @('cue4parse-gltf-bone-rotation', 'mesh-lod-sections', 'cue4parse-batch-gc', 'acl-legacy-1x', 'material-param-names', 'acl-codec-from-handle', 'actorx-fixed-width-name-clamp', 'additive-pose-bone-count-clamp', 'actorx-socket-bones-per-lod', 'acl-refpose-out-of-range', 'wwise-zero-eventid-bank-walk')
foreach ($p in $patches) {
    git -C FModel-dev/CUE4Parse apply --whitespace=nowarn "$PWD/source/patches/$p.patch"
    if ($LASTEXITCODE -ne 0) { throw "patch $p did not apply" }
}
```

The order matters in two places, and `patches/README.md` says why: `mesh-lod-sections` goes before
`cue4parse-batch-gc` (both edit `MeshConverter.cs`), and `acl-legacy-1x` goes before `acl-refpose-out-of-range`
(both edit `AnimConverter.cs`). `patches/README.md` also describes what each patch fixes. The patch files
must keep their LF line endings; `source/.gitattributes` makes sure of that.

## 4. The ACL 1.x sources

One patch (`acl-legacy-1x`) adds a decoder for the older ACL animation format, which needs the 1.3
sources beside the 2.x ones. They are not in the patch -- there are hundreds of files -- so cut them out of the
submodule you just fetched:

```powershell
$ext = "$PWD/FModel-dev/CUE4Parse/CUE4Parse-Natives/ACL/external"
git -C "$ext/acl" archive --format=zip -o "$ext/acl1.zip" v1.3.5
Expand-Archive "$ext/acl1.zip" -DestinationPath "$ext/acl1"
git -C "$ext/acl/external/rtm" archive --format=zip -o "$ext/rtm1.zip" f4ec074cc4e5f07c26f71cf7cdf3c5644ec0da46
Expand-Archive "$ext/rtm1.zip" -DestinationPath "$ext/acl1/external/rtm" -Force
Remove-Item "$ext/acl1.zip", "$ext/rtm1.zip"
```

CMake finds `acl1` on its own.

## 5. Build

```powershell
cd source
dotnet build ZeroCompanyModStudio.slnx -c Release
```

The first build restores the packages and runs CMake for the native library, so it takes a few minutes.

The programs end up in:

```
source\ZeroCompanyModStudio.Avalonia\bin\Release\net10.0\win-x64\ZeroCompanyModStudio.exe
source\ZeroCompanyLevelEditor\bin\Release\net10.0\win-x64\ZeroCompanyLevelEditor.exe
```

If you start them from a command line, `dotnet ZeroCompanyModStudio.dll` (from the same folder) works too.

## Running a build you made

- **The game's property map (a `.usmap` file) is not in the source.** The Studio looks for it in a
  `mappings` folder beside the program. The release download carries one; copy that `mappings` folder next to
  the program you built. A map describes one build of the game, so it has to match the game you have installed.
- **Oodle and Detex are fetched the first time the program decompresses game data.** They are not
  redistributable, and are not bundled (see `NOTICE.md`).
- The profile the first run is built from is `profile-template.json`, copied beside the program by the
  build. `profiles/zero-company.json` in this folder is the shipped profile with every path and key blanked.
- A fresh checkout has no `dev` folder next to the projects, so the developer command-line flags the
  projects can carry (they are compiled only when `..\dev` exists) are absent from your build, as they are
  from the release.

## What is not in this folder

The Blender add-on and the helper scripts are plain files inside every release download and are not part of
this source. Neither is the script that assembles the download.

## Licence

GNU General Public License v3.0 from version 1.6.0 onward -- see `LICENSE`. The third-party components a
build carries, and the terms they come under, are listed in `NOTICE.md`; CUE4Parse is Apache-2.0, and the
files the patches change are listed there too.
