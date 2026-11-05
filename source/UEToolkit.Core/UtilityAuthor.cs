namespace UEToolkit.Core;

using System.Text.Json.Nodes;
using CUE4Parse.FileProvider;

/// <summary>
/// Authoring a utility item: duplicating a grenade, and putting one on sale.
///
/// The counterpart of <see cref="WeaponAuthor"/> and a much smaller one, because a utility is a
/// much smaller thing. A weapon duplicate is five copies plus a minted gameplay tag plus a mesh
/// palette per attachment slot, all because a weapon's model comes out of shared palettes keyed by
/// its type tag. A utility's model is a reference on its own actor, so a duplicate is three copies
/// and no tag work at all:
///
///     UtilityItem_&lt;Id&gt;   the shop item -- name, description, icon, price
///       AssociatedGearKit  SOFT, so it moves by renameNames
///     GK_&lt;Id&gt;            the gear kit
///       ActorClass         HARD, so it moves by repointImports
///     BP_Weapon_&lt;Id&gt;      the actor, which is where the model reference lives
///
/// Which map each moves through is MEASURED, not assumed (`--copyrefs`, 2026-09-06). Getting that
/// backwards is the failure that costs a day: a rename aimed at a hard import reports success,
/// builds, installs, and still loads the original -- see
/// <see cref="CopyContents.Reference.Hard"/>.
///
/// AND A DUPLICATE IS NOT ON SALE. The Armoury sells from a list of 99 hard references
/// (<see cref="ShopTables"/>), so a correct new item nothing stocks is offered to nobody. That is
/// what <see cref="PutOnSale"/> is for, and it is the reason this file exists at all rather than
/// the duplicate being enough.
/// </summary>
public static class UtilityAuthor
{
    /// <summary>Where a mod's own packages live.</summary>
    public static string FolderFor(ModProject? mod) => WeaponAuthor.FolderFor(mod);

    /// <summary>One thing a proposed change would do, for a panel to show before it happens.</summary>
    public sealed record Change(string What, string From, string To);

    /// <summary>
    /// An id for a copy of this utility that nothing in the mod already uses.
    ///
    /// Deliberately not ending in an underscore and a number: UE splits a trailing <c>_N</c> off an
    /// FName as a NUMBER, so an asset called <c>MyBomb_2</c> mounts and nothing resolves it. See
    /// [[zero-company-fname-number-suffix]] -- the suffix here is bare, which is unaffected.
    /// </summary>
    public static string IdFor(ModProject? mod, UtilityIndex.Utility utility)
    {
        var stem = new string((utility.Name.Length > 0 ? utility.Name : utility.Key)
            .Where(char.IsLetterOrDigit).ToArray());
        if (stem.Length == 0) stem = "Utility";
        var id = stem + "Copy";
        var n = 2;
        while (mod is not null && mod.Copies.Any(c => c.Id.Equals($"UtilityItem_{id}",
                                                                  StringComparison.OrdinalIgnoreCase)))
            id = stem + "Copy" + n++;
        return id;
    }

    /// <summary>
    /// Duplicate a utility item into a mod: its shop entry, its gear kit and its actor.
    ///
    /// THREE COPIES, NOT FIVE. The ability set and the ability are deliberately left pointing at
    /// the game's, because sharing them is right by default -- a re-skinned thermal detonator that
    /// does exactly what a thermal detonator does is what somebody duplicating one usually wants,
    /// and copying the ability would silently detach the copy from any balance change the game
    /// makes. Giving it numbers of its own is a separate, explicit step.
    ///
    /// The ACTOR is copied even though nothing about it changes yet, and that is the point: the
    /// model reference lives on the actor's component template, so an author who wants their own
    /// grenade model needs a copy of the actor to point at it. Sharing the game's actor would mean
    /// the only way to change the model is to overwrite the game's grenade.
    ///
    /// Returns the same shape <see cref="WeaponAuthor.MakePlayable"/> does -- a <c>copies</c> array
    /// -- so a panel adds it the same way.
    /// </summary>
    public static JsonObject? Duplicate(IFileProvider provider, UtilityIndex.Utility utility,
                                        ModProject? mod, string id, out string? why)
    {
        why = null;
        if (utility.Item.Length == 0)
        { why = $"{utility.Describe} has no shop item to duplicate"; return null; }
        if (utility.GearKit.Length == 0)
        { why = $"{utility.Describe} names no gear kit, so there is no chain to copy"; return null; }
        if (id.Length == 0) { why = "the copy needs a name"; return null; }

        var folder = FolderFor(mod);
        var itemId = $"UtilityItem_{id}";
        var kitId = $"GK_{id}";
        var actorId = $"BP_Weapon_{id}";

        // The gear kit, pointed at the copy's own actor. Both maps: the rename covers any soft use
        // of the name, the repoint moves the import -- the same belt and braces OwnStats uses, and
        // for the same reason. Writing only one of the two is the failure that looks correct.
        var kit = new JsonObject
        {
            ["id"] = kitId,
            ["template"] = utility.GearKit,
            ["newGuid"] = true,
        };
        if (utility.Actor.Length > 0)
        {
            kit["renameNames"] = new JsonObject
            {
                [utility.Actor] = folder + actorId,
                [Leaf(utility.Actor)] = actorId,
            };
            kit["repointImports"] = new JsonObject { [utility.Actor] = actorId };
        }

        // The shop item, pointed at the copy's own gear kit. SOFT -- two names, the package path
        // and the asset name, and renaming only one leaves a reference that resolves to nothing
        // while looking edited.
        var item = new JsonObject
        {
            ["id"] = itemId,
            ["template"] = utility.Item,
            ["newGuid"] = true,
            ["display"] = utility.Name.Length > 0 ? $"{utility.Name} (copy)" : id,
            ["renameNames"] = new JsonObject
            {
                [utility.GearKit] = folder + kitId,
                [Leaf(utility.GearKit)] = kitId,
            },
        };

        var copies = new JsonArray();
        if (utility.Actor.Length > 0)
            copies.Add(new JsonObject
            {
                ["id"] = actorId,
                ["template"] = utility.Actor,
                ["newGuid"] = true,
            });
        copies.Add(kit);
        copies.Add(item);

        return new JsonObject { ["copies"] = copies };
    }

    /// <summary>
    /// A shop item this orphan could copy one from: any complete utility whose gear kit sits in the
    /// same group.
    ///
    /// GROUPED, because a shop item carries more than a name -- its ItemTags say which Armoury
    /// slot it equips into (<c>...Utilities.Equip.Throwable</c> against <c>.Health</c>), and its
    /// CharacterClassRequirements say who may carry it. A medpac's item pointed at a grenade's
    /// gear kit would equip into the medpac slot, which is the sort of thing that looks fine until
    /// somebody plays it.
    ///
    /// The orphan's own group is unknown -- it has no item, which is the whole problem -- so the
    /// kit's tags are what decide: <c>Item.Utility.Grenade</c> takes a throwable's item.
    /// </summary>
    public static UtilityIndex.Utility? DonorFor(IReadOnlyList<UtilityIndex.Utility> all,
                                                 UtilityIndex.Utility orphan)
    {
        if (!orphan.NeedsItem) return null;
        // An orphan's Tags ARE its gear kit's -- it has no item for them to come from, which is
        // what makes it an orphan.
        var kitTags = orphan.Tags;

        // The kit's own family tag -- "Grenade", "MedKit", "Serenity" -- against the family of a
        // complete one's kit. Nothing to match on falls through to the throwables, which is where
        // every orphan measured so far belongs, and the panel says which donor it chose.
        var family = kitTags
            .Select(t => t.Contains(".Item.Utility.", StringComparison.Ordinal)
                ? t[(t.LastIndexOf('.') + 1)..] : null)
            .FirstOrDefault(x => x is { Length: > 0 });

        var candidates = all.Where(u => u.Complete && u.Item.Length > 0 && u.Buyable).ToList();
        if (family is not null)
        {
            var same = candidates.FirstOrDefault(
                u => u.TypeTag.Contains(family, StringComparison.OrdinalIgnoreCase));
            if (same is not null) return same;
        }
        return candidates.FirstOrDefault(u => u.Slot == "Throwable") ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Give a utility the game finished but never put in a shop the item it lacks.
    ///
    /// ONE COPY. The gear kit, the actor, the model and the ability are all the game's own and
    /// already correct -- what is missing is the small data asset that names them and gives them a
    /// price. The same shape as <see cref="WeaponAuthor.MakePlayable"/>, and for the same reason:
    /// the thing standing between a finished asset and a player is smaller than it looks.
    ///
    /// The gear kit reference is SOFT, so it moves by renameNames -- both halves, the package path
    /// and the asset name.
    ///
    /// AND IT IS STILL NOT ON SALE afterwards. A shop item nothing stocks is a shop item nobody is
    /// offered; <see cref="PutOnSale"/> is the other half, and the panel has to say so.
    /// </summary>
    public static JsonObject? MakeBuyable(UtilityIndex.Utility orphan, UtilityIndex.Utility donor,
                                          ModProject? mod, string id, out string? why)
    {
        why = null;
        if (!orphan.NeedsItem)
        { why = $"{orphan.Describe} already has a shop item"; return null; }
        if (donor.Item.Length == 0 || donor.GearKit.Length == 0)
        { why = $"{donor.Describe} has no shop item to copy"; return null; }
        if (id.Length == 0) { why = "the new item needs a name"; return null; }

        var folder = FolderFor(mod);
        return new JsonObject
        {
            ["copies"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = $"UtilityItem_{id}",
                    ["template"] = donor.Item,
                    ["newGuid"] = true,
                    ["display"] = orphan.Name.Length > 0 ? orphan.Name : id,
                    // Pointed at the ORPHAN's gear kit, which is the game's own and stays the
                    // game's. Nothing is copied but the item.
                    ["renameNames"] = new JsonObject
                    {
                        [donor.GearKit] = orphan.GearKit,
                        [Leaf(donor.GearKit)] = Leaf(orphan.GearKit),
                    },
                },
            },
        };
    }

    /// <summary>What a duplicate would do, for a panel to show before doing it.</summary>
    public static List<Change> DuplicatePlan(ModProject? mod, UtilityIndex.Utility utility, string id)
    {
        var folder = FolderFor(mod);
        var plan = new List<Change>();
        if (utility.Actor.Length > 0)
            plan.Add(new Change("its own actor", Leaf(utility.Actor), folder + $"BP_Weapon_{id}"));
        plan.Add(new Change("its own gear kit", Leaf(utility.GearKit), folder + $"GK_{id}"));
        plan.Add(new Change("its own shop item", Leaf(utility.Item), folder + $"UtilityItem_{id}"));
        plan.Add(new Change("shares the game's", Leaf(utility.Ability),
                            "its ability and stats are the game's own"));
        return plan;
    }

    // ------------------------------------------------------------------ getting it into the shop

    /// <summary>
    /// One slot a mod could take over, with the item currently in it.
    ///
    /// Offered as a LIST rather than a free-text index because the index alone means nothing to
    /// anybody: "tier 1, slot 15, currently the Ion Blaster" is a decision a person can make.
    /// </summary>
    public sealed record Vacancy(string Table, string TableLeaf, int Index, string Item,
                                 string ItemLeaf, string Tier)
    {
        public string Describe => $"{Tier} -- currently {ItemLeaf}";
    }

    /// <summary>
    /// Every utility slot in the game's shop tables, newest tier last.
    ///
    /// Filtered to the UTILITY tables: the same six tables also stock weapon modifications, and
    /// putting a grenade in a modification slot would offer it in the Black Market's mod list.
    /// Told apart by what is IN the slot rather than by the table's name, because the name is a
    /// convention and the contents are the fact.
    /// </summary>
    public static List<Vacancy> Vacancies(IFileProvider provider)
    {
        var found = new List<Vacancy>();
        foreach (var slot in ShopTables.Of(provider))
        {
            if (slot.IsGroup) continue;
            if (!slot.ItemLeaf.StartsWith(UtilityIndex.ItemPrefix, StringComparison.Ordinal)) continue;
            found.Add(new Vacancy(slot.Table, slot.TableLeaf, slot.Index, slot.Item, slot.ItemLeaf,
                                  TierOf(slot.TableLeaf)));
        }
        return found.OrderBy(v => v.Tier, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(v => v.ItemLeaf, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>"ShopTable_MS32_Utility_Tier2" -> "Tier 2", or the table's own name if it is not one.</summary>
    private static string TierOf(string tableLeaf)
    {
        var at = tableLeaf.LastIndexOf("Tier", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? tableLeaf : "Tier " + tableLeaf[(at + 4)..];
    }

    /// <summary>
    /// Put an item on sale by taking over one of the game's shop slots.
    ///
    /// AN OVERRIDE, NOT A COPY, and it has to be: the tables are the game's, and a copy of one is a
    /// table nothing consults -- the root table names the three tiers by hard import, so a copied
    /// tier is unreachable without overriding the root anyway.
    ///
    /// One redirect, on the slot's current occupant. This REPLACES what that slot sold rather than
    /// adding a row -- which is why the panel names the item being displaced and this returns it
    /// in <paramref name="displaced"/>.
    ///
    /// AND ROW GROWTH DOES NOT FIX IT, which is worth saying because it now exists:
    /// <see cref="DataTableEdit.AddRow"/> can lengthen a cooked table, and
    /// <see cref="CharacterAuthor.Add"/> uses it to add a recruit without displacing one. A shop
    /// table cannot be treated the same way. A roster row names its character by SOFT path, so a
    /// copied row is re-pointed by rewriting two names; a shop row names its item by HARD IMPORT,
    /// and a copied row shares its template's import-map entry. Adding a shop row therefore sells
    /// a second copy of whatever the template sold, and re-pointing that import moves BOTH rows.
    /// Giving the new row an item of its own needs the IMPORT MAP grown -- a new entry, a new
    /// store-entry package id and a new export hash -- and that is not built.
    ///
    /// TWO MODS TAKING THE SAME SLOT COLLIDE, and the build already says so: two overrides of one
    /// package is the collision report's whole subject.
    /// </summary>
    public static ModOverride? PutOnSale(ModProject mod, Vacancy slot, string itemPackage,
                                         out string? displaced, out string? why)
    {
        why = null;
        displaced = slot.ItemLeaf;

        if (itemPackage.Length == 0) { why = "there is no item to sell"; return null; }
        if (string.Equals(itemPackage, slot.Item, StringComparison.OrdinalIgnoreCase))
        { why = $"{slot.ItemLeaf} is already what that slot sells"; return null; }

        // One override per table, so taking two slots in the same tier does not produce two
        // overrides of one package -- which the installer reports as a collision, correctly.
        var ov = mod.Overrides.FirstOrDefault(
            x => string.Equals(x.Target, slot.Table, StringComparison.OrdinalIgnoreCase));
        if (ov is null)
        {
            ov = new ModOverride { Target = slot.Table };
            mod.Overrides.Add(ov);
        }

        if (ov.RepointImports.ContainsKey(slot.Item))
        { why = $"this mod already put something in {slot.ItemLeaf}'s slot"; return null; }

        ov.RepointImports[slot.Item] = itemPackage;
        return ov;
    }

    private static string Leaf(string path) =>
        path.Length == 0 ? "" : path[(path.LastIndexOf('/') + 1)..];
}
