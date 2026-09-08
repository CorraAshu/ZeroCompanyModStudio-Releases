# Zero Company Mod Studio

A mod editor for **Star Wars: Zero Company**. It reads the game's own files, copies what is
already in there, and writes a mod the game loads on startup.

**No Unreal Engine. No coding. No modding experience.** Your first mod is about fifteen
minutes, most of it waiting for the tool to read the game once.

**[Download the latest release](../../releases/latest)** &middot; Windows 64-bit &middot; unzip
and run `ZeroCompanyModStudio.exe`

---

## What you can make with it

| | |
|---|---|
| **Armour and clothing** | Copy any part in the game, including gear that is hidden from the menu, and wear it on anyone. Rex's armour for the whole squad is the worked example in the guide. |
| **Your own models** | Import an FBX with your own mesh and textures. A Blender add-on ships with the tool: browse the game from inside Blender and import a piece complete with its skeleton, weights and textures, so you start from the real thing. |
| **Weapons** | Duplicate a whole weapon &mdash; all five assets it is made of &mdash; onto a shelf of its own in the Armoury. Give it your model, your icon, your numbers. |
| **Balance changes** | Damage, range, critical chance and the rest, on any weapon, with the game's own value shown beside yours while you work. |
| **Classes** | Copy a specialization and rebuild its focus tree from any of the game's 826 abilities. |
| **Attachments** | Rename, re-cost and re-stat any of the 64 weapon modifications. |
| **Battle droids as recruits** | Turn a B1, B2 or BX commando into a squad member who fights with his own animations, his own weapon and his own action points. |

Everything is made by copying something the game already has, so a part you make behaves like a
part the game shipped. **Nothing belonging to the game is overwritten.** A mod is a folder in
`SWZeroCompany\Mods`; deleting it removes the mod completely.

## Getting started

1. Download and unzip anywhere. There is no installer.
2. Run `ZeroCompanyModStudio.exe`. It finds your game, asks you to confirm once, and reads it.
3. Open **Handbook &rarr; Your First Mod** in the menu bar and follow it.

Both guides ship inside the download and are also on the web:

- **[Your First Mod](https://claude.ai/code/artifact/bf1099df-b672-4e4d-bad0-cb7bdfec051a)** &mdash; start here
- **[The Complete Handbook](https://claude.ai/code/artifact/28a8f782-b287-4f37-be1d-4665e30829c4)** &mdash; every feature, and why each one works the way it does

## Before you build

**Close the game first.** A running game holds its own files open, and the install will wait for
it rather than half-finish.

The tool builds your mod, reads the files back, and checks them before anything reaches the game.
If that check fails, nothing is installed and it says why in plain words. That is deliberate: a
mod that is already known to be broken should not get as far as your game folder.

## What it needs

- Windows 64-bit
- Star Wars: Zero Company, installed
- About 170 MB of disk once unzipped
- An internet connection the first time it decompresses game data, which fetches two components
  that cannot be redistributed

Nothing else. No .NET install, no runtime, no dependencies.

## This repository

Downloads and nothing else. The source is not public.

Bug reports and questions are welcome in [Issues](../../issues). If something in a guide does not
match what you see on screen, trust the screen and say so &mdash; that is a bug in the tool or in
the guide rather than something you did wrong.

## Licence

See [LICENSE](LICENSE). Free to download and use, including for mods you go on to share; not to
be redistributed, sold, modified or claimed as someone else's work.

The build ships third-party components under their own licences, listed in full in `NOTICE.md`
inside the download.

Star Wars, Zero Company and all related marks belong to their respective owners. This is an
unofficial tool, not affiliated with or endorsed by Bit Reactor, Respawn Entertainment,
Electronic Arts or Lucasfilm.
