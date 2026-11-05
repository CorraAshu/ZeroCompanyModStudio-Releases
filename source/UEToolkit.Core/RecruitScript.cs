namespace UEToolkit.Core;

using System.Text;

/// <summary>
/// A RECRUIT SCRIPT a mod can carry: UE4SS Lua that recruits the mod's characters straight from
/// their pawn class, past the recruitment screen.
///
/// Why it exists (2026-09-07). A character offered through the hiring pool is ROLLED: the game
/// throws the definition's class part, tactical spec and weapon spec out and fills the slots with
/// hero parts -- and it did so even when copies of those parts were legal in both the package and
/// the asset registry (two in-game tests, the same result). A battle droid so rolled moves with
/// human animations and, its basics riding on the tactical spec it lost, has no action points.
///
/// The Playable Droideka mod's answer is a script: BrunoRosterStatics.CreateAndRecruitCharacter
/// on the ENEMY pawn class makes the character with the pawn's own definition and rolls nothing;
/// then the mod's definition goes on as a preset and the actor is moved to the player's team.
/// Its readme: "the .pak alone is not enough". This writes that script for any mod whose
/// recruits spawn on an enemy pawn, and puts it where UE4SS looks.
/// </summary>
public static class RecruitScript
{
    public const string DefaultRecruitKey = "F10";
    public const string DefaultRepairKey = "F11";

    /// <summary>One character the script can recruit.</summary>
    public sealed record Entry(string Name, string PawnPackage, string PawnClass,
                               string DefinitionPackage, string DefinitionName);

    /// <summary>Every definition copy whose hiring-pool row names an enemy pawn, in mod order.</summary>
    public static List<Entry> EntriesOf(ModProject mod)
    {
        var found = new List<Entry>();
        var folder = UtilityAuthor.FolderFor(mod);
        foreach (var copy in mod.Copies)
        {
            if (!copy.Id.StartsWith("CD_Char_", StringComparison.Ordinal)) continue;
            var row = CharacterAuthor.RowOf(mod, copy);
            if (row is null || !row.Set.TryGetValue("CharacterClass", out var pawn) || pawn.Length == 0) continue;
            if (!pawn.Contains("Char_Enemy_", StringComparison.OrdinalIgnoreCase)) continue;
            var dot = pawn.LastIndexOf('.');
            if (dot <= 0) continue;

            var name = copy.Id["CD_Char_".Length..];
            if (CharacterAuthor.NamePartOf(mod, copy) is { } np &&
                np.Text.TryGetValue("FirstName", out var first) && first.Trim().Length > 0)
                name = (first.Trim() + " " + (np.Text.TryGetValue("LastName", out var last) ? last.Trim() : "")).Trim();

            found.Add(new Entry(name, pawn[..dot], pawn[(dot + 1)..], folder + copy.Id, copy.Id));
        }
        return found;
    }

    /// <summary>Whether a script would have anything to recruit.</summary>
    public static bool Applies(ModProject mod) => EntriesOf(mod).Count > 0;

    /// <summary>A key name UE4SS knows: F1..F12 or a single letter or digit. Anything else falls back to the default.</summary>
    public static string KeyOrDefault(string? key, string fallback)
    {
        var k = (key ?? "").Trim().ToUpperInvariant();
        if (k.Length == 1 && char.IsLetterOrDigit(k[0])) return char.IsDigit(k[0]) ? "NUM_" + DigitName(k[0]) : k;
        if (k.Length is 2 or 3 && k[0] == 'F' && int.TryParse(k[1..], out var n) && n is >= 1 and <= 12) return k;
        return fallback;
    }

    private static string DigitName(char c) => c switch
    {
        '0' => "ZERO", '1' => "ONE", '2' => "TWO", '3' => "THREE", '4' => "FOUR",
        '5' => "FIVE", '6' => "SIX", '7' => "SEVEN", '8' => "EIGHT", _ => "NINE",
    };

    /// <summary>The script, as text. Empty when the mod has nothing to recruit.</summary>
    public static string Generate(ModProject mod)
    {
        var entries = EntriesOf(mod);
        if (entries.Count == 0) return "";
        var recruitKey = KeyOrDefault(mod.RecruitKey, DefaultRecruitKey);
        var repairKey = KeyOrDefault(mod.RepairKey, DefaultRepairKey);
        var title = mod.DisplayName.Length > 0 ? mod.DisplayName : mod.Name;

        var sb = new StringBuilder();
        sb.Append("-- ").Append(title).AppendLine(": recruit its characters straight from their pawn class, past the");
        sb.AppendLine("-- recruitment screen and the roll that strips a droid of its own class, tactical spec and weapon spec.");
        sb.AppendLine("-- Written by Zero Company Mod Studio. Needs UE4SS.");
        sb.AppendLine("--");
        sb.Append("--   ").Append(recruitKey).Append("   in the Den: recruit the next one (cycles): ")
          .AppendLine(string.Join(", ", entries.Select(e => e.Name)));
        sb.Append("--   ").Append(repairKey).AppendLine("   repair pass: re-apply each one's definition and put them on your team");
        sb.AppendLine("--");
        sb.AppendLine("-- The recipe is the Playable Droideka mod's (Nexus 56): CreateAndRecruitCharacter on the enemy");
        sb.AppendLine("-- pawn class makes the character with the pawn's own definition and rolls nothing; then the");
        sb.AppendLine("-- mod's definition goes on as a preset and the actor changes team.");
        sb.AppendLine();
        sb.AppendLine("local UEHelpers = require(\"UEHelpers\")");
        sb.Append("local TAG = ").Append(Q("[" + mod.Name + "] ")).AppendLine();
        sb.AppendLine("local function log(m) print(TAG .. tostring(m) .. \"\\n\") end");
        sb.AppendLine();
        sb.AppendLine("local CHARACTERS = {");
        foreach (var e in entries)
            sb.Append("    { name = ").Append(Q(e.Name))
              .Append(", pawnPkg = ").Append(Q(e.PawnPackage))
              .Append(", pawnCls = ").Append(Q(e.PawnClass))
              .Append(", cdPkg = ").Append(Q(e.DefinitionPackage))
              .Append(", cdName = ").Append(Q(e.DefinitionName)).AppendLine(" },");
        sb.AppendLine("}");
        sb.AppendLine("local nextIndex = 1");
        sb.AppendLine();
        sb.AppendLine(Body.Replace("{RECRUIT}", recruitKey).Replace("{REPAIR}", repairKey)
                          .Replace("{COUNT}", entries.Count.ToString()));
        return sb.ToString();
    }

    private static string Q(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private const string Body = @"local function findStatics(paths)
    for _, p in ipairs(paths) do
        local o = StaticFindObject(p)
        if o and o:IsValid() then return o end
    end
    return nil
end

-- An asset the game has not loaded yet is found nowhere; asking the asset registry for it
-- makes it resident, and then StaticFindObject sees it. The mod's own registry is loaded by
-- the game's mod runtime, so the mod's definitions answer here too.
local function registryLoad(pkg, name, findPath)
    local o = StaticFindObject(findPath)
    if o and o:IsValid() then return o end
    local helpers = StaticFindObject(""/Script/AssetRegistry.Default__AssetRegistryHelpers"")
    if not helpers or not helpers:IsValid() then error(""AssetRegistryHelpers not found"") end
    local ad = { [""PackageName""] = UEHelpers.FindOrAddFName(pkg), [""AssetName""] = UEHelpers.FindOrAddFName(name) }
    pcall(function() return helpers:GetAsset(ad) end)
    o = StaticFindObject(findPath)
    if o and o:IsValid() then return o end
    error(""not resident after GetAsset: "" .. findPath)
end

-- The pawns of one class that are OURS: in the level, and active roster characters.
local function rosterPawnsOf(pawnCls)
    local out = {}
    local roster = StaticFindObject(""/Script/Bruno.Default__BrunoRosterStatics"")
    local pawns = FindAllOf(pawnCls)
    if not pawns or not roster then return out end
    for _, p in ipairs(pawns) do
        if p:IsValid() and tostring(p:GetFullName()):find(""PersistentLevel"") then
            local ok, ours = pcall(function() return roster:IsActiveRosterCharacter(p) end)
            if ok and ours then table.insert(out, p) end
        end
    end
    return out
end

-- The mod's definition as a preset (name, faction, whatever the mod changed) and the player's
-- team, on every roster pawn of this class. The Droideka mod's F6, made per character.
local function repair(entry)
    local list = rosterPawnsOf(entry.pawnCls)
    log(""repair "" .. entry.name .. "": "" .. #list .. "" pawn(s)"")
    if #list == 0 then return 0 end
    local cd = registryLoad(entry.cdPkg, entry.cdName, entry.cdPkg .. ""."" .. entry.cdName)
    local cstat = findStatics({ ""/Script/BitReactorCore.Default__CustomizationStatics"",
                                ""/Script/BitReactorGame.Default__CustomizationStatics"" })
    local gs = findStatics({ ""/Script/BitReactorGame.Default__BitReactorGameStatics"",
                             ""/Script/BitReactorCore.Default__BitReactorGameStatics"" })
    local us = findStatics({ ""/Script/BitReactorGame.Default__BitReactorGameStatics_Unit"",
                             ""/Script/BitReactorCore.Default__BitReactorGameStatics_Unit"" })
    for i, p in ipairs(list) do
        if cstat and cd then
            local okA = pcall(function() return cstat:ApplyCustomizationDefinitionAsPresetToCharacter(p, cd) end)
            log(""  ["" .. i .. ""] definition applied: "" .. tostring(okA))
        end
        if gs and us then
            local okH, hostile = pcall(function() return gs:GetHostileTeam(p) end)
            if okH and hostile then
                local okT = pcall(function() return us:ChangeActorTeam(p, hostile, true) end)
                log(""  ["" .. i .. ""] team changed: "" .. tostring(okT))
            end
        end
    end
    return #list
end

local function recruit(entry)
    ExecuteInGameThread(function()
        local ok, err = pcall(function()
            local cls = registryLoad(entry.pawnPkg, entry.pawnCls, entry.pawnPkg .. ""."" .. entry.pawnCls)
            local statics = StaticFindObject(""/Script/Bruno.Default__BrunoRosterStatics"")
            local pc = UEHelpers.GetPlayerController()
            local c = statics:CreateAndRecruitCharacter(pc, cls, {}, false, false)
            log(""recruited "" .. entry.name .. "": "" .. tostring(c and c:IsValid() and c:GetFullName() or ""null""))
        end)
        if not ok then log(""recruit FAILED: "" .. tostring(err)) end
    end)
    -- The Den spawns the new pawn a moment later; then the mod's definition and team go on.
    -- If the pawn is not there yet, the repair key does the same by hand.
    ExecuteWithDelay(2000, function()
        ExecuteInGameThread(function()
            local ok, err = pcall(function() repair(entry) end)
            if not ok then log(""repair FAILED: "" .. tostring(err)) end
        end)
    end)
end

RegisterKeyBind(Key.{RECRUIT}, function()
    local entry = CHARACTERS[nextIndex]
    nextIndex = nextIndex % #CHARACTERS + 1
    recruit(entry)
end)

RegisterKeyBind(Key.{REPAIR}, function()
    ExecuteInGameThread(function()
        for _, entry in ipairs(CHARACTERS) do
            local ok, err = pcall(function() repair(entry) end)
            if not ok then log(""repair FAILED: "" .. tostring(err)) end
        end
    end)
end)

log(""loaded: {RECRUIT} recruits ({COUNT} character(s), cycling), {REPAIR} repairs"")
";

    // ------------------------------------------------------------------ where UE4SS keeps its mods

    /// <summary>The game's Binaries/Win64, or null when the paks path does not sit in a project.</summary>
    public static string? BinariesFor(string paks) =>
        ModInstall.ProjectDirectory(paks) is { } p ? Path.Combine(p, "Binaries", "Win64") : null;

    /// <summary>
    /// Whether UE4SS is installed: the proxy DLL, its own DLL, or an override.txt beside the exe
    /// (the two-file install that points UE4SS at a folder elsewhere).
    /// </summary>
    public static bool UE4SSPresent(string paks)
    {
        var bin = BinariesFor(paks);
        if (bin is null) return false;
        return File.Exists(Path.Combine(bin, "dwmapi.dll")) ||
               File.Exists(Path.Combine(bin, "UE4SS.dll")) ||
               File.Exists(Path.Combine(bin, "ue4ss", "UE4SS.dll")) ||
               File.Exists(Path.Combine(bin, "override.txt"));
    }

    /// <summary>
    /// The folder UE4SS scans for script mods. The standard install keeps it at
    /// Binaries/Win64/ue4ss/Mods; an install by override.txt keeps everything, Mods included, in
    /// the folder the override names -- UE4SS's working directory follows the override.
    /// </summary>
    public static string? ModsFolderFor(string paks)
    {
        var bin = BinariesFor(paks);
        if (bin is null) return null;
        var over = Path.Combine(bin, "override.txt");
        if (File.Exists(over))
        {
            var target = File.ReadAllLines(over).Select(l => l.Trim())
                             .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (target is { Length: > 0 }) return Path.Combine(target.Trim('"'), "Mods");
        }
        return Path.Combine(bin, "ue4ss", "Mods");
    }

    /// <summary>Write the script where UE4SS looks, and say where; the folder written is returned.</summary>
    public static string Install(string paks, string modName, string lua, Action<string>? log = null)
    {
        var mods = ModsFolderFor(paks)
            ?? throw new DirectoryNotFoundException("the game's Binaries/Win64 could not be found from the paks folder");
        var dir = Path.Combine(mods, modName);
        WriteTo(dir, lua);
        log?.Invoke($"   recruit script -> {dir}");
        if (!UE4SSPresent(paks))
            log?.Invoke("   UE4SS is NOT installed in Binaries/Win64 (no dwmapi.dll, UE4SS.dll or " +
                        "override.txt), so the script does nothing until it is");
        return dir;
    }

    /// <summary>Remove the script folder, if there is one. True when something was removed.</summary>
    public static bool Uninstall(string paks, string modName, Action<string>? log = null)
    {
        var mods = ModsFolderFor(paks);
        if (mods is null) return false;
        var dir = Path.Combine(mods, modName);
        if (!Directory.Exists(dir)) return false;
        Directory.Delete(dir, recursive: true);
        log?.Invoke($"   removed the recruit script at {dir}");
        return true;
    }

    /// <summary>
    /// A copy INSIDE the mod's own folder -- UE4SS/Mods/&lt;Mod&gt;/ -- so a mod handed to somebody
    /// else carries its script and a line saying where it goes. The game's mod runtime scans a
    /// mod folder for containers only, so this does it no harm.
    /// </summary>
    public static void Bundle(string pluginDir, string modName, string lua)
    {
        var dir = Path.Combine(pluginDir, "UE4SS", "Mods", modName);
        WriteTo(dir, lua);
        File.WriteAllText(Path.Combine(pluginDir, "UE4SS", "README.txt"),
            "This mod recruits its characters by a UE4SS script. Copy the Mods folder beside this file\r\n" +
            "into <game>\\SWZeroCompany\\Binaries\\Win64\\ue4ss\\ (UE4SS must be installed), then in the Den\r\n" +
            "press the recruit key named at the top of Mods\\" + modName + "\\Scripts\\main.lua.\r\n",
            new UTF8Encoding(false));
    }

    private static void WriteTo(string dir, string lua)
    {
        Directory.CreateDirectory(Path.Combine(dir, "Scripts"));
        File.WriteAllText(Path.Combine(dir, "Scripts", "main.lua"), lua, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "enabled.txt"), "", new UTF8Encoding(false));
    }
}
