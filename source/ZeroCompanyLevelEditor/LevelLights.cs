using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using UEToolkit.Core;

namespace ZeroCompanyLevelEditor;

/// <summary>
/// THE LEVEL'S OWN LIGHT RIG, READ RATHER THAN INVENTED.
///
/// A viewport can always make up a light, and a made-up light is why a grey level looks like a
/// grey level. The real rig is sitting in the map: Umbara's Lighting sublevel carries three
/// DirectionalLightComponents, a SkyLight, a SkyAtmosphere, an ExponentialHeightFog, a
/// PostProcess and fifteen spot lights -- with the sun's own direction (P=316 Y=188), colour
/// (F2FFF9) and intensity (8.409) written down.
///
/// So this reads them. Nothing here shades anything; it is the data layer, and it prints what it
/// found so the numbers can be checked against the game's own before anything is drawn with them.
///
/// UNITS AND CONVENTIONS, because every one of these is a way to be confidently wrong:
///   * A light points along its own +X after rotation. UE has no separate direction field.
///   * `LightColor` is an FColor in sRGB; `Intensity` is separate and NOT premultiplied.
///   * A light is usually attached to its actor's root, so the world rotation has to come from
///     the composed attach chain, not from RelativeRotation. Three of Umbara's sit under
///     Blueprint roots.
/// </summary>
public static class LevelLights
{
    public sealed record Sun(Vector3 Direction, Vector3 Colour, float Intensity, float Indirect)
    {
        public override string ToString() =>
            $"sun dir {Direction.X:0.00},{Direction.Y:0.00},{Direction.Z:0.00}  " +
            $"colour {Colour.X:0.00},{Colour.Y:0.00},{Colour.Z:0.00}  " +
            $"intensity {Intensity:0.###}  indirect {Indirect:0.##}";
    }

    public sealed record Sky(Vector3 Colour, float Intensity)
    {
        public override string ToString() =>
            $"sky colour {Colour.X:0.00},{Colour.Y:0.00},{Colour.Z:0.00}  intensity {Intensity:0.###}";
    }

    /// <summary>
    /// A local light -- spot or point.
    ///
    /// UNITS ARE NOT OPTIONAL HERE. A directional light's Intensity is in LUX, which is an
    /// irradiance and can be added straight to the shading sum. A local light's is in CANDELAS or
    /// LUMENS, which are not: a candela becomes lux only after dividing by the square of the
    /// distance in METRES, and a lumen needs a further 1/4pi. Treating 5,000 cd as though it were
    /// 5,000 lux is a light three thousand times brighter than the sun, which is why `Units` is
    /// read from the component rather than assumed.
    ///
    /// `IsPoint` because a point light is the same maths with no cone, and levels mix them; a
    /// point light shaded as a spot with a default 44-degree cone lights a wedge of the room.
    /// </summary>
    public sealed record Spot(Vector3 At, Vector3 Direction, Vector3 Colour,
                              float Intensity, float Radius, float Inner, float Outer,
                              string Units, bool InverseSquared, bool IsPoint, float FalloffExp)
    {
        /// <summary>
        /// Intensity restated as LUX AT ONE METRE, so it can be divided by distance-squared in
        /// metres and land in the same units as the sun. This is the photometric definition, not
        /// a fit: candelas already are lux-metres-squared, and lumens spread over a sphere.
        /// </summary>
        public float LuxAtOneMetre => Units.Contains("Lumen", StringComparison.OrdinalIgnoreCase)
            ? Intensity / (4f * MathF.PI)
            : Units.Contains("EV", StringComparison.OrdinalIgnoreCase)
                ? MathF.Pow(2f, Intensity) * 0.125f          // EV100 -> cd, the usual 1/8 factor
                : Intensity;                                  // candelas, and UE's default

        public override string ToString() =>
            $"{(IsPoint ? "point" : "spot ")} at {At.X:0},{At.Y:0},{At.Z:0}  " +
            $"dir {Direction.X:0.00},{Direction.Y:0.00},{Direction.Z:0.00}  " +
            $"intensity {Intensity:0.#} {Units}  ({LuxAtOneMetre:0.#} lux@1m)  " +
            $"radius {Radius:0}  cone {Inner:0}/{Outer:0}" +
            (InverseSquared ? "" : $"  NOT inverse-squared, exp {FalloffExp:0.#}");
    }

    public sealed record Fog(Vector3 Colour, float Density, float HeightFalloff,
                            float StartDistance, float Height)
    {
        public override string ToString() =>
            $"fog colour {Colour.X:0.00},{Colour.Y:0.00},{Colour.Z:0.00}  density {Density:0.####}  " +
            $"falloff {HeightFalloff:0.####}  start {StartDistance:0}  height {Height:0}";
    }

    /// <summary>Everything the level says about how it is lit. Any part may be absent.</summary>
    public sealed record Rig(List<Sun> Suns, Sky? Sky, List<Spot> Spots, Fog? Fog, bool HasAtmosphere)
    {
        public Sun? Key => Suns.OrderByDescending(s => s.Intensity).FirstOrDefault();

        public IEnumerable<string> Describe()
        {
            if (Suns.Count == 0) yield return "no directional light in this level";
            foreach (var s in Suns) yield return s.ToString();
            if (Sky is not null) yield return Sky.ToString();
            if (Fog is not null) yield return Fog.ToString();
            if (HasAtmosphere) yield return "sky atmosphere present";
            // "local", not "spot": the list holds point lights too, and a report that calls a
            // point light a spot is how a 44-degree default cone would have gone unquestioned.
            yield return $"{Spots.Count} local light(s), " +
                         $"{Spots.Count(x => x.IsPoint)} of them point lights";
            foreach (var s in Spots.OrderByDescending(x => x.LuxAtOneMetre).Take(6)) yield return "   " + s;
        }
    }

    /// <summary>
    /// ONE RIG OUT OF MANY, because a map ships several and only one of them is on.
    ///
    /// MEASURED, not assumed. `--lightscan` over three maps:
    ///   * the HUB streams HUB_Lighting_EXT_Day, _Dawn and _Night, each with its OWN sun, sky and
    ///     fog, plus a vista rig for the Ring of Kafrene. Merging them lit the hub with FOUR
    ///     directional lights at once -- a red one, two blue ones and a white one -- and picked
    ///     whichever fog happened to stream last.
    ///   * MM_02_030_TheDarkGrotto ships _Lighting_WIP (230 lights) beside _ConstructionLighting,
    ///     whose sun is intensity 80 against a sky of 0.01. That is a development rig; summing it
    ///     with the real one is a floodlight in a cave.
    ///   * Umbara's _Blockout sublevel carries two more.
    ///
    /// This is the same fault the author diagnosed for geometry -- "are you loading every instance
    /// of this map at once" -- one layer down, and it was still doing it after that fix because
    /// the fix collapsed LEVEL INSTANCES and a lighting sublevel is not one.
    ///
    /// So: SUN, SKY AND FOG COME FROM EXACTLY ONE SUBLEVEL. Local lights come from all of them
    /// except the ones dropped outright, because a corridor's fixtures belong whatever the time of
    /// day. Every choice is returned in `Notes` -- a rig picked silently is a rig nobody can check,
    /// and the pick is a heuristic over names.
    /// </summary>
    public static Rig Choose(IReadOnlyList<(string Sublevel, Rig Rig)> parts,
                             IReadOnlyDictionary<string, string>? family,
                             List<string>? notes = null)
    {
        // A DEVELOPMENT RIG IS NOT THE LEVEL'S RIG. These sublevels exist so the team can work in
        // a greybox; the game never streams them with the shipping lighting.
        static bool Development(string name) =>
            name.Contains("Construction", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Blockout", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Greybox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("_Debug", StringComparison.OrdinalIgnoreCase);

        // A VISTA LIGHTS ITSELF. The Ring of Kafrene hangs a million units off the hub and carries
        // its own sun so the backdrop reads; using it on the interior lights the room from space.
        static bool Backdrop(string name) =>
            name.Contains("Vista", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Nebula", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Skybox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("RingOf", StringComparison.OrdinalIgnoreCase);

        // A variant the plan did not pick. The dialog already asks which state of the map to load;
        // once "Day" wins, "Night"'s sun must go with it or the choice was cosmetic.
        bool Lost(string name) =>
            family is not null && family.TryGetValue(Bare(name), out var win) &&
            !win.Equals(Bare(name), StringComparison.OrdinalIgnoreCase);

        var live = new List<(string Sublevel, Rig Rig)>();
        foreach (var part in parts)
        {
            if (Development(part.Sublevel))
            { notes?.Add($"ignored {part.Sublevel}: a development rig"); continue; }
            if (Lost(part.Sublevel))
            { notes?.Add($"ignored {part.Sublevel}: not the variant chosen"); continue; }
            live.Add(part);
        }

        // WHICH SUBLEVEL OWNS THE ENVIRONMENT. Only the ones that actually carry a sun, a sky or a
        // fog are candidates -- the hub's main HUB_Lighting has 131 fixtures and no sun at all, so
        // "the one with the most lights" would have picked a rig with no environment in it.
        var candidates = live.Where(x => x.Rig.Suns.Count > 0 || x.Rig.Sky is not null || x.Rig.Fog is not null)
                             .ToList();
        var chosen = candidates
            .OrderBy(x => Backdrop(x.Sublevel) ? 1 : 0)
            .ThenByDescending(x => x.Rig.Suns.Count > 0 ? 1 : 0)
            .ThenByDescending(x => x.Rig.Spots.Count)
            .ThenBy(x => x.Sublevel, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        foreach (var x in candidates)
            if (x.Sublevel != chosen.Sublevel)
                notes?.Add($"ignored the environment in {x.Sublevel}: " +
                           $"{x.Rig.Suns.Count} sun(s){(x.Rig.Fog is null ? "" : ", fog")} -- " +
                           $"{chosen.Sublevel} owns it");

        if (chosen.Rig is not null) notes?.Add($"environment from {chosen.Sublevel}");

        return new Rig(
            chosen.Rig?.Suns ?? new List<Sun>(),
            chosen.Rig?.Sky,
            live.SelectMany(x => x.Rig.Spots).ToList(),
            chosen.Rig?.Fog,
            live.Any(x => x.Rig.HasAtmosphere));
    }

    private static string Bare(string name) =>
        name.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;

    /// <summary>
    /// Read the rig out of one sublevel's exports.
    ///
    /// The transforms come from the same composed attach chain the placements use, so a light
    /// parented under a Blueprint root lands where the level puts it rather than at its own
    /// relative offset -- which for a rotation is the difference between a sun and a torch.
    /// </summary>
    public static Rig Read(UObject[] exports)
    {
        var w = LevelExport.Compose(exports, inherit: true);
        var suns = new List<Sun>();
        var spots = new List<Spot>();
        Sky? sky = null;
        Fog? fog = null;
        var atmosphere = false;

        for (var i = 0; i < exports.Length; i++)
        {
            var e = exports[i];
            var cls = e?.ExportType ?? "";
            if (e is null) continue;

            // A LIGHT THE LEVEL HAS SWITCHED OFF MUST NOT LIGHT ANYTHING. Umbara ships THREE
            // directional lights and one of them -- pink, intensity 7.07 -- carries
            // bAffectsWorld=False. Summing all three lights the level from three directions in
            // colours it never uses, and the result looks like a shading bug rather than a
            // reading one. Checked before anything else, for every light class.
            if (!Enabled(e)) continue;

            if (cls.Contains("DirectionalLightComponent", StringComparison.Ordinal))
                suns.Add(new Sun(Forward(w.Rot[i]), ColourOf(e), Float(e, "Intensity", 1f),
                                 Float(e, "IndirectLightingIntensity", 1f)));

            else if (cls.Contains("SkyLightComponent", StringComparison.Ordinal))
                sky = new Sky(ColourOf(e), Float(e, "Intensity", 1f));

            else if (cls.Contains("SpotLightComponent", StringComparison.Ordinal) ||
                     cls.Contains("PointLightComponent", StringComparison.Ordinal))
                spots.Add(new Spot(
                    new Vector3((float) w.Loc[i].X, (float) w.Loc[i].Y, (float) w.Loc[i].Z),
                    Forward(w.Rot[i]), ColourOf(e),
                    Float(e, "Intensity", 1f),
                    Float(e, "AttenuationRadius", 1000f),
                    Float(e, "InnerConeAngle", 0f),
                    Float(e, "OuterConeAngle", 44f),
                    Enum(e, "IntensityUnits", "Candelas"),
                    e.GetOrDefault("bUseInverseSquaredFalloff", true),
                    cls.Contains("PointLightComponent", StringComparison.Ordinal),
                    Float(e, "LightFalloffExponent", 8f)));

            else if (cls.Contains("ExponentialHeightFogComponent", StringComparison.Ordinal))
                // The height the falloff is measured FROM is the component's own world Z -- an
                // exponential height fog anchored at the origin instead sits under the level and
                // never reaches it.
                fog = new Fog(FogColour(e), Float(e, "FogDensity", 0.02f),
                              Float(e, "FogHeightFalloff", 0.2f),
                              Float(e, "StartDistance", 0f),
                              (float) w.Loc[i].Z);

            else if (cls.Contains("SkyAtmosphereComponent", StringComparison.Ordinal))
                atmosphere = true;
        }

        return new Rig(suns, sky, spots, fog, atmosphere);
    }

    /// <summary>
    /// Where a light points: its own +X, rotated into the world.
    ///
    /// UE stores no direction on a light at all -- it is the component's forward vector, so a rig
    /// read without composing the attach chain points every light the same way and looks like a
    /// shading bug rather than a reading one.
    /// </summary>
    private static Vector3 Forward(FQuat q)
    {
        var f = q.RotateVector(new FVector(1, 0, 0));
        var v = new Vector3((float) f.X, (float) f.Y, (float) f.Z);
        return v.LengthSquared() < 1e-6f ? new Vector3(0, 0, -1) : Vector3.Normalize(v);
    }

    /// <summary>
    /// Is this light actually on?
    ///
    /// Three separate flags can switch one off and they mean different things, so all three are
    /// checked: `bAffectsWorld` false is a light the level has disabled outright, `bVisible` false
    /// is a component turned off, and `bHiddenInGame` is one kept for the editor only. Any of them
    /// means the game does not light with it, which is the only question being asked here.
    /// </summary>
    private static bool Enabled(UObject e)
    {
        try
        {
            if (!e.GetOrDefault("bAffectsWorld", true)) return false;
            if (!e.GetOrDefault("bVisible", true)) return false;
            if (e.GetOrDefault("bHiddenInGame", false)) return false;
        }
        catch { /* a light that will not answer is treated as on, which is UE's own default */ }
        return true;
    }

    /// <summary>An enum property as its own name. Cooked levels write these as FName, not int.</summary>
    private static string Enum(UObject e, string name, string fallback)
    {
        try
        {
            var raw = e.Properties.FirstOrDefault(p => p.Name.Text == name)?.Tag?.GenericValue?.ToString();
            if (string.IsNullOrEmpty(raw)) return fallback;
            // "ELightUnits::Candelas" -> "Candelas"
            var cut = raw.LastIndexOf(':');
            return cut >= 0 ? raw[(cut + 1)..] : raw;
        }
        catch { return fallback; }
    }

    private static float Float(UObject e, string name, float fallback)
    {
        try { return e.GetOrDefault(name, fallback); }
        catch { return fallback; }
    }

    /// <summary>
    /// A light's colour, LINEARISED.
    ///
    /// `LightColor` is an FColor in sRGB and the shading maths wants linear. Skipping this is the
    /// mistake that makes everything look washed out and slightly wrong in a way nobody can name.
    /// </summary>
    private static Vector3 ColourOf(UObject e)
    {
        var c = LightColourOf(e);
        // TIMES ITS COLOUR TEMPERATURE, which nothing read until 2026-09-18. The hub's lights
        // carry temperatures from 2,841 K to 10,000 K, and the colour of a light that uses one is
        // NOT `LightColor` alone -- a 3,000 K work lamp and a 9,000 K floodlight came out the
        // same colour as each other.
        var k = TemperatureTint(e);
        return new Vector3(c.X * k.X, c.Y * k.Y, c.Z * k.Z);
    }

    /// <summary>
    /// A LIGHT'S COLOUR TEMPERATURE, and the switch that decides whether it counts at all.
    ///
    /// `Temperature` is written on 280 of the hub's lights across 21 levels. **Only 93 use it.**
    /// `bUseTemperature` defaults to FALSE and UE writes a property only when it differs from its
    /// default, so the 96 instances that mention the switch are the entire population that could
    /// be using one -- 93 True, 3 False. The other ~187 authored a temperature and left it
    /// switched off.
    ///
    /// Applying all 280 would have recoloured a third of the hub's lighting on the strength of a
    /// number the game ignores -- the same shape as `Metallic Strength` shipping 0 and
    /// `Use tint only? (No DIF)` shipping true. Read the switch, never the value alone.
    ///
    /// NORMALISED so the brightest channel is 1. A temperature is a HUE; brightness is
    /// `Intensity`, applied separately, and scaling by both counts it twice.
    /// </summary>
    private static Vector3 TemperatureTint(UObject e)
    {
        try
        {
            if (!e.GetOrDefault("bUseTemperature", false)) return Vector3.One;
            var k = Float(e, "Temperature", 6500f);
            return k <= 0f ? Vector3.One : Kelvin(k);
        }
        catch { return Vector3.One; }
    }

    /// <summary>
    /// Kelvin to linear sRGB. THE CURVE ITSELF MOVED to Rendering/Kelvin.cs, unchanged, because
    /// MM_Emissive turned out to need the same fit for a material parameter rather than a light
    /// and a second copy of a colour curve is this repo's favourite bug waiting to happen. Kept
    /// as a forwarder so the light rig's call sites read the same as before.
    /// </summary>
    public static Vector3 Kelvin(float kelvin) => UEToolkit.App.Rendering.Kelvin.ToRgb(kelvin);

    private static Vector3 LightColourOf(UObject e)
    {
        try
        {
            if (e.GetOrDefault<FColor?>("LightColor", null) is { } c)
                return new Vector3(ToLinear(c.R), ToLinear(c.G), ToLinear(c.B));
            if (e.GetOrDefault<FLinearColor?>("LightColor", null) is { } l)
                return new Vector3(l.R, l.G, l.B);
        }
        catch { /* a light with no colour is white, which is UE's own default */ }
        return Vector3.One;
    }

    private static Vector3 FogColour(UObject e)
    {
        try
        {
            if (e.GetOrDefault<FLinearColor?>("FogInscatteringLuminance", null) is { } l)
                return new Vector3(l.R, l.G, l.B);
            if (e.GetOrDefault<FLinearColor?>("FogInscatteringColor", null) is { } c)
                return new Vector3(c.R, c.G, c.B);
        }
        catch { /* fall through to a neutral grey */ }
        return new Vector3(0.5f, 0.55f, 0.6f);
    }

    private static float ToLinear(byte v)
    {
        var s = v / 255f;
        return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }
}
