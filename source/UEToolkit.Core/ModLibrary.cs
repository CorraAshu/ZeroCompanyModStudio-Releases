namespace UEToolkit.Core;

/// <summary>
/// Where a person's mods live, and what is in them.
///
/// Until this existed the editor had a text box you typed a folder path into, and it started
/// empty. That is fine for the one person who built the tool and knows there is a `Mods` folder
/// beside a `.uproject`; for anybody else the first screen of a mod editor was a blank path to a
/// folder that did not exist yet.
///
/// So there is a default location, it is created on demand, and a mod is a file in it. The
/// location stays configurable because someone keeping mods in a git repo or on another drive is
/// a reasonable thing to be doing -- the default just has to be right often enough that nobody
/// has to think about it.
///
/// Documents rather than beside the executable, deliberately: it survives replacing the tool,
/// needs no administrator rights, and is somewhere a person can find their own files without
/// being told where they went.
/// </summary>
public static class ModLibrary
{
    /// <summary>What this build calls its default mods folder. See <see cref="Branding"/>.</summary>
    public static string FolderName => Branding.ModsFolder;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FolderName);

    /// <summary>
    /// One mod on disk, summarised without opening the editor.
    ///
    /// <paramref name="Unreadable"/> is set instead of the entry being dropped. A project that
    /// silently fails to appear is indistinguishable from one that was never saved, and the person
    /// looking for it has no way to tell which happened -- so a broken file is listed, named, and
    /// says what is wrong with it.
    /// </summary>
    public sealed record Entry(string Path, string Name, string DisplayName, string Author,
                               string Version, int Parts, DateTime Modified, string? Unreadable,
                               string What = "", string Folder = "",
                               // WHAT THE CARD CAN SHOW A PICTURE OF, worked out here because
                               // this is the only place the project is already open. IconFile is
                               // the mod's OWN artwork, which is the right picture whenever there
                               // is one; IconFrom names templates whose icons stand in when there
                               // is not. See DrawModCard.
                               string IconFile = "",
                               IReadOnlyList<string>? IconFrom = null)
    {
        public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

        public string Describe => Unreadable is not null
            ? "cannot be read: " + Unreadable
            : $"{Parts} part(s)" +
              (string.IsNullOrWhiteSpace(Author) ? "" : $", by {Author}") +
              (string.IsNullOrWhiteSpace(Version) ? "" : $", v{Version}");
    }

    /// <summary>
    /// The folder to use: the configured one when it is set, otherwise the default. Created if it
    /// is not there, because a chooser pointed at a folder that does not exist can only say "no
    /// mods", which is true and useless.
    /// </summary>
    public static string Resolve(string? configured)
    {
        var folder = string.IsNullOrWhiteSpace(configured) ? DefaultFolder : configured!;
        try { Directory.CreateDirectory(folder); }
        catch (Exception) { /* an unwritable folder still lists; saving will say so */ }
        return folder;
    }

    /// <summary>Every mod in a folder, newest first -- the one being worked on is the one wanted.</summary>
    /// <summary>
    /// Rename a saved mod: its name, and the file it lives in.
    ///
    /// What it does NOT rename is the folder its assets are built into. That is stored rather than
    /// derived, and ModProject says why at length: a package that moves gets a new id, so a mod
    /// that already works would quietly become a different mod. Someone renaming a typo would
    /// break the copy they have installed, and nothing would say so. The container name follows on
    /// its own, because that IS derived from the name.
    /// </summary>
    public static bool Rename(string path, string newName, out string? why)
    {
        why = null;
        newName = (newName ?? "").Trim();

        if (newName.Length == 0) { why = "A mod needs a name."; return false; }
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            why = @"A name cannot contain any of  \ / : * ? "" < > |";
            return false;
        }

        var source = Path.GetFullPath(path);
        if (!File.Exists(source)) { why = "That mod file is no longer there."; return false; }

        var target = Path.Combine(Path.GetDirectoryName(source)!, newName + ".json");
        var samePlace = string.Equals(target, source, StringComparison.OrdinalIgnoreCase);
        if (!samePlace && File.Exists(target))
        { why = $"There is already a mod called {newName}."; return false; }

        try
        {
            var mod = ModProject.Load(source);
            var was = mod.Name;
            mod.Name = newName;

            // The list shows DisplayName when there is one, and a newly created mod gets one equal
            // to its name -- so renaming only Name renames the file and changes nothing anybody
            // sees, which reads as a rename button that does not work. Followed only while it
            // TRACKED the name: a display name somebody chose is theirs, not ours to overwrite.
            if (string.IsNullOrWhiteSpace(mod.DisplayName) ||
                string.Equals(mod.DisplayName, was, StringComparison.Ordinal))
                mod.DisplayName = newName;

            // Written before the old one is removed. The other order loses the mod outright if the
            // write fails, and this way the worst case is two copies rather than none.
            mod.MoveTo(target);
            mod.Save();
            if (!samePlace) File.Delete(source);
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    public static List<Entry> List(string folder)
    {
        var found = new List<Entry>();
        if (!Directory.Exists(folder)) return found;

        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var modified = DateTime.MinValue;
            try { modified = File.GetLastWriteTime(file); } catch (Exception) { }

            try
            {
                var mod = ModProject.Load(file);
                found.Add(new Entry(file, mod.Name, mod.DisplayName, mod.Author, mod.Version,
                                    mod.Parts.Count, modified, null, WhatItIs(mod), mod.Folder,
                                    OwnPicture(mod), TemplatePictures(mod)));
            }
            catch (Exception e)
            {
                found.Add(new Entry(file, Path.GetFileNameWithoutExtension(file), "", "", "",
                                    0, modified, e.Message));
            }
        }

        found.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return found;
    }

    /// <summary>
    /// The mod's own artwork, if it has any: the first icon file a part or a copy names.
    ///
    /// Preferred over anything borrowed, because it is the picture the author chose and the one
    /// the mod actually shows in game.
    /// </summary>
    private static string OwnPicture(ModProject mod) =>
        // THE ONE THE AUTHOR CHOSE first, if they chose one. Everything after it is a guess made
        // on their behalf -- a good guess, and still the reason there had to be a way to say.
        !string.IsNullOrWhiteSpace(mod.Image) ? mod.Image
        : mod.Parts.Select(x => x.Icon).Concat(mod.Copies.Select(x => x.Icon))
             .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) ?? "";

    /// <summary>
    /// Templates whose icon can stand in for a mod that has no artwork of its own.
    ///
    /// A LIST rather than the first one, because the first copy is often something with no icon
    /// at all -- a character definition, a pawn, an animation set -- and a card would then be
    /// blank for a mod full of parts. Customization parts come first: those are the ones the menu
    /// draws, so those are the ones with pictures.
    ///
    /// Capped, because each one the panel tries costs a package read -- paid once and cached, at
    /// two reads a frame, so a long list settles over a second rather than stalling a frame.
    /// </summary>
    private static List<string> TemplatePictures(ModProject mod)
    {
        var all = mod.Copies.Select(c => c.Template)
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToList();

        // ORDERED BY WHAT THE MENU DRAWS, because that is what has a thumbnail. An outfit part
        // and a weapon's armoury part have one; a gear kit, a barrel and a weapon actor do not,
        // and a first pass that preferred every "CPD_" put CPD_WP_Barrel_01_Default at the front
        // for two mods that both had a rifle in them.
        //
        // Still only an ordering, never a filter: the panel tries them in turn and stops at the
        // first that yields a picture, so being wrong here costs a package read, not a blank card.
        static int Rank(string t)
        {
            var leaf = t[(t.LastIndexOf('/') + 1)..];
            if (leaf.StartsWith("CPD_H_", StringComparison.OrdinalIgnoreCase) ||
                leaf.StartsWith("CD_", StringComparison.OrdinalIgnoreCase)) return 0;
            if (leaf.StartsWith("CPD_", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }
        return all.OrderBy(Rank).Take(12).ToList();
    }

    /// <summary>
    /// What a mod IS, in a phrase somebody would say out loud.
    ///
    /// "1 part(s), v1.0" describes the FILE. Nobody thinks of their work that way -- they made a
    /// weapon, or a helmet, or two changes to the game's own things -- and a list of mods that
    /// cannot say which is which is a list you have to open one by one.
    ///
    /// Counts rather than names, because a mod holding three of something should not read as a
    /// mod holding one. A weapon is counted by its ARMOURY part: the other eight assets in the
    /// chain are the same weapon, and counting them would call one rifle nine things.
    /// </summary>
    public static string WhatItIs(ModProject mod)
    {
        var bits = new List<string>();

        var weapons = mod.Copies.Count(c => WeaponAuthor.IdentityOf(c.Id) is not null);
        if (weapons > 0) bits.Add(weapons == 1 ? "a weapon" : $"{weapons} weapons");

        if (mod.Specs.Count > 0)
            bits.Add(mod.Specs.Count == 1 ? "a specialization" : $"{mod.Specs.Count} specializations");

        // Worn things, named by the slot the author chose rather than by "part". Each slot
        // carries BOTH forms, because half of them are already plural -- deriving them gave
        // "a legs", "a arms" and "a boots".
        foreach (var group in mod.Parts.GroupBy(WornName).Where(g => g.Key is not null))
        {
            var n = group.Count();
            bits.Add(n == 1 ? group.Key!.One : string.Format(group.Key!.Many, n));
        }

        // REBALANCED GROUPS, counted from the AUTHOR'S list and not from what it expands into.
        //
        // A mod that only scales enemy stats has no parts, no copies and no overrides in the file
        // -- all of those appear at build time -- so without this it reads "nothing in it yet",
        // which is the card telling an author their work is not there.
        var groups = mod.EnemyStats.Count(e => e.Multipliers.Count > 0);
        if (groups > 0)
            bits.Add(groups == 1 ? "an enemy group rebalanced" : $"{groups} enemy groups rebalanced");

        if (mod.Overrides.Count > 0)
            bits.Add(mod.Overrides.Count == 1
                ? "1 change to the game's own things"
                : $"{mod.Overrides.Count} changes to the game's own things");

        return bits.Count == 0 ? "nothing in it yet" : string.Join(" · ", bits);
    }

    /// <summary>How a slot is said, one of them and several. Null where there is no good word.</summary>
    private sealed record Worn(string One, string Many);

    private static Worn? WornName(ModPart p) => p.Slot switch
    {
        "Helmet" => new Worn("a helmet", "{0} helmets"),
        "Torso"  => new Worn("a torso piece", "{0} torso pieces"),
        "Arms"   => new Worn("arms", "{0} sets of arms"),
        "Legs"   => new Worn("legs", "{0} sets of legs"),
        "Boots"  => new Worn("boots", "{0} sets of boots"),
        "Head"   => new Worn("a head", "{0} heads"),
        "Hair"       => new Worn("a hairstyle", "{0} hairstyles"),
        "FacialHair" => new Worn("facial hair", "{0} kinds of facial hair"),
        "Lekku"      => new Worn("a lekku style", "{0} lekku styles"),
        "Horns"      => new Worn("horns", "{0} sets of horns"),
        "Antennae"   => new Worn("antennae", "{0} sets of antennae"),
        "Eyebrows"   => new Worn("eyebrows", "{0} sets of eyebrows"),
        "Pack"       => new Worn("a backpack", "{0} backpacks"),
        // Said as a droid's, because "a head" beside "a helmet" in the same sentence would read
        // as the person's.
        "AstromechHead" => new Worn("a droid dome", "{0} droid domes"),
        "AstromechBody" => new Worn("a droid body", "{0} droid bodies"),
        "AstromechLegs" => new Worn("droid legs", "{0} sets of droid legs"),
        _ => null,
    };

    /// <summary>
    /// A file path for a new mod with this name, or null with a reason.
    ///
    /// Refusing an existing name here rather than at save time is the difference between "pick
    /// another name" and silently replacing somebody's work.
    /// </summary>
    public static string? PathFor(string folder, string name, out string? why)
    {
        why = null;
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) { why = "give the mod a name"; return null; }

        var safe = new string(trimmed.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim();
        if (safe.Length == 0) { why = "that name has no characters a file can use"; return null; }

        var path = Path.Combine(folder, safe + ".json");
        if (File.Exists(path)) { why = $"{safe}.json already exists"; return null; }
        return path;
    }
}
