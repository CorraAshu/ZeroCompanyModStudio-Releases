using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// ONE GROUP OF ENEMIES, and the numbers this mod puts on them.
///
/// THE WHOLE FEATURE IS ONE SENTENCE, and it is Core's:
/// `CPD_TacticalSpec_Enemy_Generic_Marauder` is used by 15 characters — 12 Outlaw and 3 Pyke —
/// and the stat tables it points at belong to the Separatist BX Droid Commando. Every table in
/// this game has been editable for months, so an author could always have found those tables and
/// halved them; they would have halved the droids too, silently, with nothing anywhere to say so.
///
/// So this screen shows WHO EACH GROUP AFFECTS, read from the 277 enemy character definitions,
/// and the build COPIES the tables rather than editing them. The blast radius is not a tooltip —
/// it is the reason the screen exists, so it sits in the sheet where it cannot be missed.
///
/// MULTIPLIERS, NOT NUMBERS. A group is up to 32 tables and a health change alone is 16 of them,
/// one per tier and difficulty. A multiplier keeps the game's own curve — a T4 Insane enemy stays
/// harder than a T1 Easy one — which is what "make Outlaws weaker" means and is not what typing
/// sixteen numbers produces.
/// </summary>
public sealed class EnemyTab : SheetTab
{
    public EnemyTab(EnemyStats.Group group)
    {
        Group = group;
    }

    /// <summary>The tactical spec this tab rebalances, with its tables and its users.</summary>
    public EnemyStats.Group Group { get; }

    public override string Title => Group.Display;

    public override string Subtitle => Group.Leaf;

    public override string Tooltip => Group.Describe;

    public override string Aside => Group.Users.Count == 0
        ? "no character uses it"
        : $"{Group.Users.Count} character(s): {string.Join(", ", Group.Factions)}";

    public override bool CanEdit => true;

    /// <summary>
    /// This is not a copy of anything — it is a change to how a set of the game's enemies fight,
    /// made by copying their tables so nobody else reading them is affected.
    /// </summary>
    public override bool IsCopy => true;

    public override string EditBadge => "A GROUP OF ENEMIES";

    public override string EditWarning =>
        $"Its {Group.Tables.Count} stat table(s) are COPIED into your mod, not edited — so a " +
        "group that shares its tables with another faction changes on its own, and the other " +
        "faction is left alone.";

    /// <summary>
    /// The one state in which changing anything here changes nothing in game.
    ///
    /// Not every tactical spec is worn by somebody. A group with no users builds, installs, reads
    /// back clean and does nothing — which is indistinguishable from a build that failed unless
    /// it is said here.
    /// </summary>
    public override string Problem => Group.Users.Count == 0
        ? "No character in the game uses this group, so changing its numbers changes nothing you " +
          "can see. It builds, installs and does nothing."
        : "";

    public override IReadOnlyList<object> Sheet
    {
        get
        {
            var blocks = new List<object>();
            var band = false;

            Fact("Tactical spec", Group.Leaf);
            Fact("Stat tables", Group.Tables.Count.ToString());
            Fact("Factions", Group.Factions.Count > 0
                ? string.Join(", ", Group.Factions) : "—");

            // WHO, BY NAME. "15 characters" is the number somebody needs to decide; the names
            // are what they need to check the decision was the one they meant.
            blocks.Add(new SheetBand
            {
                Text = Group.Users.Count == 0 ? "Used by — nobody"
                                              : $"Used by ({Group.Users.Count})"
            });
            foreach (var u in Group.Users.Take(24))
                blocks.Add(new SheetBullet { Text = u });
            if (Group.Users.Count > 24)
                blocks.Add(new SheetBullet { Text = $"and {Group.Users.Count - 24} more" });

            // THE TABLES, WITH WHEN EACH ONE APPLIES. A group's 32 tables are one per tier and
            // difficulty, and seeing that is what makes "a multiplier, not a number" obvious.
            if (Group.Tables.Count > 0)
            {
                blocks.Add(new SheetBand { Text = $"Its tables ({Group.Tables.Count})" });
                foreach (var set in Group.Tables.GroupBy(x => x.Set))
                    blocks.Add(new SheetBullet
                    {
                        Text = $"{set.Key} — {set.Count()}: " +
                               string.Join(", ", set.Select(x => x.When).Distinct().Take(8))
                    });
            }

            return blocks;

            void Fact(string label, string? value)
            {
                if (value is not { Length: > 0 }) return;
                blocks.Add(new SheetFact { Label = label, Value = value, Band = band });
                band = !band;
            }
        }
    }
}
