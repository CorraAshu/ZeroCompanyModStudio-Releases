using Avalonia.Media.Imaging;
using CUE4Parse.FileProvider;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// One row of a section's list: a group heading, or a thing.
///
/// HEADINGS ARE ROWS, not a grouped ItemsControl. The author's own drawing of this list has the
/// class names sitting inline above their weapons — "Longarm" then four weapons, "Pistol" then
/// seven — and a flat list with heading rows is what that is. It also keeps one selection and one
/// template, which a nested grouping does not.
/// </summary>
public sealed class SectionItem : Obs
{
    public required string Name { get; init; }

    /// <summary>Which heading it belongs under. The headings themselves are made by the view model.</summary>
    public string Group { get; init; } = "";

    /// <summary>A heading, like "Longarm". Not selectable and drawn differently.</summary>
    public bool IsHeader { get; init; }

    /// <summary>
    /// Something a mod can actually start from.
    ///
    /// The ImGui browser's own "only duplicatable" test: the chain is complete, or the only thing
    /// missing is a gear kit — which is two small assets rather than a dead end. 102 weapons ship
    /// and most of them are enemy variants sharing another weapon's chain, so without this the
    /// list is four times longer than the part of it anybody works with.
    /// </summary>
    public bool Usable { get; init; } = true;

    /// <summary>What is worth saying about it — "no gear kit — not playable".</summary>
    public string Note { get; init; } = "";

    /// <summary>
    /// Words the filter box also looks in, that the row does not show. A modification is found by
    /// what it DOES ("Critical Hit") and by its family ("Condenser") as well as by its name, and
    /// neither belongs on a row that has to stay one line.
    /// </summary>
    public string Keywords { get; init; } = "";

    /// <summary>
    /// What tells this row apart from its neighbours, when the name does not.
    ///
    /// 1,178 wearable parts share a few dozen display names between them — twenty consecutive
    /// rows reading "Gloves" is a list that cannot be used. The game's own id is what separates
    /// them, so it rides alongside rather than replacing a name somebody can read.
    /// </summary>
    public string Detail { get; init; } = "";

    /// <summary>A heading's open state. Only headings use it.</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>
    /// The heading's fold arrow. COMPUTED, not notified, exactly as IsOpen is: toggling a group
    /// rebuilds SectionRows, so the row carrying this is a new one and there is nothing to notify.
    /// </summary>
    public Avalonia.Media.Geometry Chevron => Chevrons.For(IsOpen);

    /// <summary>The object path of this row's picture, or empty.</summary>
    public string IconPath { get; init; } = "";

    private Bitmap? _icon;
    private bool _asked;

    /// <summary>
    /// The picture, decoded ON FIRST ASK.
    ///
    /// The getter starting the work is deliberate: a virtualised list only binds the rows it
    /// realises, so "the first time this is read" IS "the first time somebody could see it".
    /// Anything eager here would decode over a thousand textures to fill twenty rows.
    /// </summary>
    public Bitmap? Icon
    {
        get
        {
            if (!_asked && IconPath.Length > 0)
            {
                _asked = true;
                IconLoader.Want(IconPath, b =>
                {
                    _icon = b;
                    Raise(nameof(Icon));
                    Raise(nameof(HasIcon));
                });
            }
            return _icon;
        }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>The index record behind it, for whatever edits it later.</summary>
    public object? Payload { get; init; }

    /// <summary>
    /// THIS MOD CHANGES IT, or ships a copy of it.
    ///
    /// Settable rather than init, because it changes when an edit is made and the section list
    /// is not rebuilt for that — a row that only learned it had changed on the next rebuild
    /// would be wrong for exactly as long as somebody was looking at what they had just done.
    /// </summary>
    private bool _changed;
    public bool Changed { get => _changed; set => Set(ref _changed, value); }
}

/// <summary>
/// One tab in the strip: the Home Page, or one of the sections.
///
/// The author's shape, in his words: *"can we add a Home page and then add
/// the tabs in there so they aren't so big but still easy to see? ... So it'd be like Home Page,
/// Weapons, Clothing, Utilities…"*. This is what replaces the row of nine buttons he disliked —
/// the same nine choices, in the strip the reference puts them in, next to the assets he has open.
/// </summary>
public sealed class SectionTab : Obs
{
    public required string Title { get; init; }
    public required SectionKind Kind { get; init; }

    private bool _active;
    public bool IsActive { get => _active; set => Set(ref _active, value); }
}

public enum SectionKind
{
    /// <summary>The normal Data Explorer. *"if we hit Home page it'd take us back to what we see now"*.</summary>
    Home,
    Weapons, Clothing, Utilities, Attachments, Abilities, Tables, Recruits, Classes, Specs, Missions, Enemies,

    /// <summary>The game's Wwise events and manifest media, to hear and to extract.</summary>
    Sounds,
}

/// <summary>
/// WHAT EACH SECTION LISTS, read from the Core index that already answers it.
///
/// None of this is new knowledge. WeaponIndex, UtilityIndex, AbilityIndex, TableIndex,
/// PartsCatalog, CharacterLibrary and MissionIndex were all written for the ImGui Studio's
/// panels; every one of them takes a provider and hands back a list. So a section is a VIEW of an
/// index, and the work here is arranging it — not working out what a weapon is again.
/// </summary>
public static class Sections
{
    /// <summary>
    /// The sections that read the PARTS CATALOGUE and therefore have to wait for it.
    ///
    /// A LIST, not a test written twice. The wait was keyed to Clothing alone and Specs came up
    /// empty — "Specs: 0 read" — because the catalogue was still building when its loader ran.
    /// Nothing threw, the count was honest, and the section simply looked like the game had no
    /// specializations in it. Read correctly, dropped before use, one more time.
    /// </summary>
    public static bool NeedsCatalog(SectionKind kind)
        => kind is SectionKind.Clothing or SectionKind.Classes or SectionKind.Specs;

    /// <summary>
    /// Sounds need the library scanned, which is its own index over the paks and the Wwise
    /// manifests. Asked separately from the parts catalogue because it is a different read and
    /// only one section wants it.
    /// </summary>
    public static bool NeedsSounds(SectionKind kind) => kind is SectionKind.Sounds;

    /// <summary>The strip, in the author's order.</summary>
    public static IReadOnlyList<(string Title, SectionKind Kind)> All { get; } = new[]
    {
        ("Home Page", SectionKind.Home),
        ("Weapons",   SectionKind.Weapons),
        ("Clothing",  SectionKind.Clothing),
        ("Utilities", SectionKind.Utilities),
        ("Attachments", SectionKind.Attachments),
        ("Abilities", SectionKind.Abilities),
        ("Tables",    SectionKind.Tables),
        ("Recruits",  SectionKind.Recruits),
        ("Classes",   SectionKind.Classes),
        ("Specs",     SectionKind.Specs),
        ("Missions",  SectionKind.Missions),
        ("Enemies",   SectionKind.Enemies),
        ("Sounds",    SectionKind.Sounds),
    };

    /// <summary>
    /// Read one section. ON A BACKGROUND THREAD — every one of these walks the file index.
    ///
    /// A section that is not wired yet says so, and says WHICH index will feed it. That is worth
    /// more than an empty list: it tells whoever reads it next where to start, and it cannot be
    /// mistaken for "this section is empty".
    /// </summary>
    public static List<SectionItem> Load(SectionKind kind, IFileProvider provider,
                                         PartsCatalog catalog, SoundLibrary sounds)
        => kind switch
        {
            SectionKind.Sounds    => Sounds(sounds),
            SectionKind.Weapons   => Weapons(provider),
            SectionKind.Clothing  => Clothing(catalog),
            SectionKind.Utilities => Utilities(provider),
            SectionKind.Attachments => Attachments(provider),
            SectionKind.Tables    => Tables(provider),
            SectionKind.Abilities => Abilities(provider),
            SectionKind.Recruits  => Recruits(provider),
            SectionKind.Classes   => Classes(catalog),
            SectionKind.Specs     => Specs(catalog),
            SectionKind.Missions  => Missions(provider),
            SectionKind.Enemies   => Enemies(provider),
            _ => NotWired(kind),
        };

    /// <summary>
    /// EVERY GROUP OF ENEMIES, sorted by how many characters read it.
    ///
    /// "Used by" is the BLAST RADIUS and belongs on the row rather than behind a hover: several
    /// groups share their stat tables with a different faction — Outlaw and Pyke Marauders read
    /// the Separatist BX Droid Commando's — so that number is what somebody needs before they
    /// commit to anything, not after.
    ///
    /// Grouped by faction, and the most-used first inside each, because a group nobody uses is a
    /// group whose numbers change nothing in the game.
    /// </summary>
    /// <summary>
    /// EVERY SOUND THE GAME CAN PLAY, grouped by the folder Wwise files it under.
    ///
    /// TWO SOURCES IN ONE LIST, and the row says which. An AkAudioEvent has a package somebody
    /// can open and re-point; a manifest entry has only a media id. They are the same thing to
    /// somebody looking for a noise and completely different things to somebody changing one,
    /// which is why the source is on the row rather than behind a hover.
    ///
    /// LOCALISED VOICE IS MARKED. The same line exists once per language, so a search for a
    /// character's name returns the same sentence a dozen times, and the only thing telling them
    /// apart is this.
    /// </summary>
    private static List<SectionItem> Sounds(SoundLibrary lib)
        => lib.Entries
              .OrderBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
              .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
              .Select(e => new SectionItem
              {
                  Group = e.Folder.Length > 0 ? e.Folder : "(no folder)",
                  Name = e.Name,
                  Detail = e.IsLocalised ? e.Language : "SFX",
                  Note = e.FromManifest ? "" : "an event, with a package behind it",
                  Payload = e,
              }).ToList();

    private static List<SectionItem> Enemies(IFileProvider provider)
        => EnemyStats.All(provider)
                     .OrderByDescending(g => g.Users.Count)
                     .ThenBy(g => g.Display, StringComparer.Ordinal)
                     .Select(g => new SectionItem
                     {
                         Group = g.Factions.Count > 0
                             ? string.Join(", ", g.Factions) : "(nobody uses these)",
                         Name = g.Display,
                         Detail = g.Users.Count == 1 ? "1 character"
                                                     : $"{g.Users.Count} characters",
                         Note = g.Tables.Count == 0 ? "no stat tables — nothing to change"
                              : g.Users.Count == 0 ? "no character uses it" : "",
                         Usable = g.Users.Count > 0 && g.Tables.Count > 0,
                         Payload = g,
                     }).ToList();

    /// <summary>Every gameplay ability, under the folder it lives in.</summary>
    private static List<SectionItem> Abilities(IFileProvider provider)
        => AbilityIndex.Of(provider, AbilityIndex.AbilityPrefix)
                       .OrderBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                       .Select(e => new SectionItem
                       {
                           Group = e.Folder.Length > 0 ? e.Folder : "(no folder)",
                           Name = e.Name,
                           Payload = e,
                       }).ToList();

    /// <summary>
    /// The recruitment pool, under the pool each row belongs to.
    ///
    /// `Active` is the game's own word for whether a row is in the rotation, and an incomplete
    /// chain means the row points at something that is not there — both are states worth seeing
    /// before choosing one to work from.
    /// </summary>
    private static List<SectionItem> Recruits(IFileProvider provider)
        => CharacterIndex.Of(provider)
                         .OrderBy(r => r.PoolLeaf, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(r => new SectionItem
                         {
                             Group = r.PoolLeaf.Length > 0 ? r.PoolLeaf : "(no pool)",
                             Name = r.Name.Length > 0 ? r.Name : r.Row,
                             Detail = r.Row,
                             Note = !r.Active ? "not in the rotation"
                                  : !r.Complete ? "chain incomplete" : "",
                             Usable = r.Complete,
                             Payload = r,
                         }).ToList();

    /// <summary>
    /// CHARACTER CLASSES AND WEAPON SPECIALISATIONS — the ImGui Studio's own restriction.
    ///
    /// It builds the Classes browser as the SAME parts panel narrowed to two slots, and says why:
    /// "a class and a weapon specialisation are two headings buried under eleven hundred outfit
    /// pieces in Parts, and they are what somebody opens the browser for when they are working on
    /// classes rather than clothes". Taken from there rather than invented here.
    /// </summary>
    private static readonly string[] ClassSlots =
        { "Character.Class", "Character.Specializations.Weapon" };

    private static List<SectionItem> Classes(PartsCatalog catalog)
        => catalog.Entries
                  .Where(e => ClassSlots.Contains(e.Part.Slot, StringComparer.Ordinal))
                  .OrderBy(e => e.Part.Slot, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                  .Select(e => new SectionItem
                  {
                      Group = e.Part.Slot == "Character.Class" ? "Classes" : "Weapon specialisations",
                      Name = e.Label,
                      Detail = string.Equals(e.Label, e.Part.Name, StringComparison.Ordinal)
                          ? "" : e.Part.Name,
                      Note = Describe(e),
                      IconPath = e.Detail.IconPath ?? "",
                      Payload = e,
                  }).ToList();

    /// <summary>
    /// SPECIALIZATIONS, TALENTS, WEAPON SPECS AND GEAR KITS, as four groups.
    ///
    /// SpecPanel's own four kinds, its own tests, and its own reason for having them: "eight are
    /// what a player calls a specialization; the other hundred and twenty are talents, weapon
    /// specialisations and gear kits. Filtering on the family showed all of them, which is a list
    /// of a hundred and fifty-one with the eight anybody wants buried in it." Its pills become
    /// this section's headings.
    /// </summary>
    private static readonly (string Group, Func<string, bool> Match)[] SpecKinds =
    {
        ("Specializations", s => s.Contains(".Specializations.Tactical.", StringComparison.Ordinal)),
        ("Talents",         s => s.EndsWith(".Specializations.Talent", StringComparison.Ordinal)),
        ("Weapon specs",    s => s.EndsWith(".Specializations.Weapon", StringComparison.Ordinal)),
        ("Gear kits",       s => s.EndsWith(".Specializations.Weapon.GearKit", StringComparison.Ordinal)),
    };

    /// <summary>
    /// Whether this catalogue entry is a SPECIALIZATION — one of the four kinds this section
    /// lists, decided by the same tests the section uses.
    ///
    /// Asked by Duplicate, so a specialization reached from the Data Explorer duplicates exactly
    /// as one reached from the list. Two copies of "is this a spec?" would be two answers.
    /// </summary>
    public static bool LooksLikeSpec(PartsCatalog.Entry e)
        => SpecKinds.Any(k => k.Match(e.Part.Slot));

    private static List<SectionItem> Specs(PartsCatalog catalog)
    {
        var rows = new List<SectionItem>();
        foreach (var (group, match) in SpecKinds)
            rows.AddRange(catalog.Entries
                .Where(e => match(e.Part.Slot))
                .OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                .Select(e => new SectionItem
                {
                    Group = group,
                    Name = e.Label,
                    Detail = string.Equals(e.Label, e.Part.Name, StringComparison.Ordinal)
                        ? "" : e.Part.Name,
                    Note = Describe(e),
                    IconPath = e.Detail.IconPath ?? "",
                    Payload = e,
                }));
        return rows;
    }

    /// <summary>Every mission, under its faction.</summary>
    private static List<SectionItem> Missions(IFileProvider provider)
        => MissionIndex.All(provider)
                       .OrderBy(m => m.Faction, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(m => m.Display, StringComparer.OrdinalIgnoreCase)
                       .Select(m => new SectionItem
                       {
                           Group = m.Faction.Length > 0 ? m.Faction : "(no faction)",
                           Name = m.Display.Length > 0 ? m.Display : m.Leaf,
                           Detail = m.Leaf,
                           Note = m.Type,
                           Payload = m,
                       }).ToList();

    /// <summary>
    /// Everything wearable, under its slot.
    ///
    /// READ FROM THE SHARED PartsCatalog, not by walking the parts again. Building it is a
    /// package read per part and the catalogue already pays that once and keeps the answer —
    /// a second walk here would double the cost of opening the Studio to save one call.
    ///
    /// "A mod can start from it" here means it HAS GEOMETRY. Only about a third of the catalogue
    /// is a wearable outfit slot with a mesh; the rest are colours, names, presets, weapon
    /// specialisations and Blueprint bases, and starting a clothing mod from one of those gives a
    /// mod that builds, cooks, installs and does nothing.
    /// </summary>
    private static List<SectionItem> Clothing(PartsCatalog catalog)
        => catalog.Entries
                  .OrderBy(e => e.Part.Slot, StringComparer.OrdinalIgnoreCase)
                  .ThenBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                  .Select(e => new SectionItem
                  {
                      Group = Slot(e.Part.Slot),
                      Name = e.Label,
                      Detail = string.Equals(e.Label, e.Part.Name, StringComparison.Ordinal)
                          ? "" : e.Part.Name,
                      // The gate is why you cannot wear it, in one word, and the species lock is
                      // the other reason a part never appears for a given character.
                      Note = Describe(e),
                      IconPath = e.Detail.IconPath ?? "",
                      Usable = CanUse(e, out _),
                      Payload = e,
                  }).ToList();

    /// <summary>
    /// A slot tag, short enough to be a heading.
    ///
    /// The tags read Character.Appearance.Astromech.Body.Mesh, and the first two words are on
    /// nearly all of them — a heading that is mostly the same prefix as every other heading is
    /// not telling anybody which one they are looking at.
    /// </summary>
    private static string Slot(string tag)
    {
        if (tag.Length == 0 || tag == "(none)") return "(no slot)";
        const string prefix = "Character.";
        var t = tag.StartsWith(prefix, StringComparison.Ordinal) ? tag[prefix.Length..] : tag;
        return t.StartsWith("Appearance.", StringComparison.Ordinal) ? t["Appearance.".Length..] : t;
    }

    private static string Describe(PartsCatalog.Entry e)
    {
        var bits = new List<string>();
        if (e.Part.Gate is { Length: > 0 } g && g != "open") bits.Add(g);
        if (e.Part.SpeciesLock is { Length: > 0 } sp) bits.Add(sp);
        if (!CanUse(e, out var why) && why is { Length: > 0 }) bits.Add(why);
        return string.Join("  ·  ", bits);
    }

    /// <summary>
    /// WHETHER A MOD CAN START FROM THIS PART — asked through the conversion itself.
    ///
    /// My own test was "has a mesh", and it was wrong the moment Specs and Classes arrived: a
    /// specialization is SUPPOSED to carry no geometry — it is abilities, stat tables and text —
    /// so every one of the 151 was labelled "no mesh" and its sheet warned that a mod built from
    /// it would show nothing. That is not a fault in the part, it is this asking the wrong
    /// question. ModPart.FromGamePart already knows which slots need a mesh and which do not, and
    /// says why when it refuses; the ImGui panel asks it for exactly this reason, in its words:
    /// "two copies of 'can this be a mod part?' drift, and the copy in the UI is the one that
    /// decides whether the button appears".
    /// </summary>
    public static bool CanUse(PartsCatalog.Entry e, out string? why)
        => ModPart.FromGamePart(e.Part.Name, e.Label, e.Part.Slot, e.Detail.MeshPath,
                                e.Detail.IconPath, e.Detail.AnimClassPath,
                                e.Detail.HidesHead, out why) is not null;

    /// <summary>
    /// Every weapon, under its class.
    ///
    /// `NeedsGearKit` is the game's own state and not a fault of ours: eight weapons ship with an
    /// actor, a loadout and a type part and NO gear kit, so the game has them and cannot equip
    /// them. Saying so in the list is the difference between "why is the E-5 greyed out" and
    /// knowing there are two small assets between it and a player.
    /// </summary>
    private static List<SectionItem> Weapons(IFileProvider provider)
        => WeaponIndex.Of(provider).Select(w => new SectionItem
        {
            // A key with no underscore has no class in it — eight ship that way, and grouping
            // them under an empty heading drew eight weapons with no heading at all.
            Group = w.Class.Length > 0 ? w.Class : "(no class in the name)",
            Name = w.Name,
            // BOTH notes, as the ImGui browser has them. "Shares another weapon's chain" is why
            // an _Enemy variant cannot be duplicated, and leaving it out makes that look
            // unexplained rather than deliberate.
            Note = w.NeedsGearKit ? "no gear kit — not playable"
                 : !w.Complete ? "shares another weapon's chain"
                 : "",
            Usable = w.Complete || w.NeedsGearKit,
            Payload = w,
        }).ToList();

    // ------------------------------------------------------------------ the 64 modifications

    private static readonly object ModificationsLock = new();
    private static IFileProvider? _modificationsFrom;
    private static List<Modifications.Entry> _modifications = new();

    /// <summary>
    /// Every weapon modification, read once per mounted game and kept. It is 128 packages (an
    /// ability and a shop item each) and about a quarter of a second, but the section, the tabs
    /// opened from it and the rarity list all want the same answer, and two reads of the game
    /// that could disagree is one too many.
    /// </summary>
    public static List<Modifications.Entry> ModificationsOf(IFileProvider provider)
    {
        lock (ModificationsLock)
        {
            if (!ReferenceEquals(_modificationsFrom, provider))
            {
                _modifications = Modifications.All(provider);
                _modificationsFrom = provider;
            }
            return _modifications;
        }
    }

    /// <summary>The slot as the Armoury names its group.</summary>
    public static string SlotTitle(string slot) => slot switch
    {
        "Stabilizer" => "Stabilizers",
        "Barrel" => "Barrels",
        "Scope" => "Scopes",
        _ => slot,
    };

    /// <summary>A number as a modification shows it: a percentage where the game's own tag says so.</summary>
    public static string StatText(double value, bool percent) =>
        percent ? NumberText.Show(value * 100, "0.######") + "%" : NumberText.Show(value, "0.######");

    /// <summary>
    /// "WHAT ATTACHMENTS DO": the barrels, cells, scopes and stabilizers a player fits in the
    /// Armoury, 64 of them, four tiers to a style and four styles to a slot.
    ///
    /// UNDER THEIR STYLE, as the 1.6.1 screen drew them: the four tiers of a style share a
    /// sentence and are exactly the four things somebody could confuse, so they sit under one
    /// heading, in tier order, with the number each carries on the row.
    /// </summary>
    private static List<SectionItem> Attachments(IFileProvider provider)
        => ModificationsOf(provider)
            .Select(e => new SectionItem
            {
                Group = $"{SlotTitle(e.Slot)}  ·  {e.Readable}",
                Name = $"T{e.Tier}  {e.Title}",
                Detail = e.Stat is null ? "inherited" : StatText(e.Stat.Value, e.Percent),
                Note = e.Stat is null ? "uses the tier below" : "",
                Keywords = $"{Modifications.Plain(e.Does)} {e.Readable} {e.Slot} {e.Style} {e.Title}",
                Payload = e,
            }).ToList();

    /// <summary>Utilities, under their slot — UtilityIndex has its own answer for that.</summary>
    private static List<SectionItem> Utilities(IFileProvider provider)
        => UtilityIndex.Of(provider)
                       .OrderBy(UtilityIndex.Group).ThenBy(u => u.Name)
                       .Select(u => new SectionItem
                       {
                           Group = UtilityIndex.Group(u),
                           Name = u.Name,
                           Payload = u,
                       }).ToList();

    /// <summary>
    /// The data tables, under the folder they live in.
    ///
    /// NAME AND FOLDER, not the record's own ToString. That placeholder printed every field of
    /// every entry as one string and the rows ran off the panel — his words, "the text strings
    /// are insanely long". An Entry knows its Name; there was never a reason to print its guts.
    /// </summary>
    private static List<SectionItem> Tables(IFileProvider provider)
        => TableIndex.Of(provider)
                     .OrderBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                     .Select(e => new SectionItem
                     {
                         Group = e.Folder.Length > 0 ? e.Folder : "(no folder)",
                         Name = e.Name,
                         Note = e.IsComposite ? "composite" : "",
                         Payload = e,
                     }).ToList();

    /// <summary>Nothing reaches this today; kept so a new SectionKind cannot silently show nothing.</summary>
    private static List<SectionItem> NotWired(SectionKind kind) => new()
    {
        new() { Group = $"{kind} is not wired yet", Name = "no index is reading it" },
    };
}
