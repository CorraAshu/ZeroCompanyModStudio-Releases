using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE PLUGINS: the Blender add-on's installer, and the Studio noticing that an add-on somebody
/// installed with an older version is out of date.
///
/// The installer itself is Core's <see cref="BlenderPlugin"/> (find Blender by its settings folder,
/// copy the add-on in, write down where the backend is), exactly as the ImGui Studio's Plugins
/// window used it. This file is what is NEW in 2.0:
///
///   - An add-on installed by 1.6.x calls <c>UEToolkit.App.exe</c>, which 2.0 no longer ships.
///     Unzipping 2.0 over an old folder leaves that program beside a library it cannot load, and
///     every import from Blender then fails with nothing but "the exporter failed". So at start
///     the Studio LOOKS (it changes nothing), and when an installed add-on is older than the one
///     it ships it offers ONE click: "Update the Blender add-on" - the installer, run again.
///   - And it removes the dead 1.6.x toolkit files from its OWN folder, saying each one in the log
///     (<see cref="OldToolkitFiles"/>: only <c>UEToolkit.App.*</c>, only below version 2.0).
/// </summary>
public sealed partial class StudioVm
{
    // ------------------------------------------------------------------ what ships

    /// <summary>Whether this build carries any plug-in at all (the menu is absent when it carries none).</summary>
    public bool HasPlugins =>
        BlenderPlugin.Source() is not null || SubstancePreset.Source() is not null ||
        MaxRetarget.Source() is not null;

    // ------------------------------------------------------------------ at start

    /// <summary>
    /// The dead 1.6.x toolkit files in the Studio's own folder, removed and said. Called once at
    /// start. Nothing is said when there are none.
    /// </summary>
    public void RemoveOldToolkitFiles()
    {
        try { OldToolkitFiles.Remove(AppContext.BaseDirectory, Say); }
        catch (Exception e) { Say("old toolkit files: " + e.Message); }
    }

    private List<BlenderPlugin.Stale> _staleAddons = [];
    private bool _addonDismissed;
    private string _addonDone = "";

    /// <summary>Whether the notice under the menu bar is showing.</summary>
    public bool HasAddonOffer => !_addonDismissed && (_staleAddons.Count > 0 || _addonDone.Length > 0);

    /// <summary>The button is there while there is something to update, and not once it is done.</summary>
    public bool CanUpdateAddon => _staleAddons.Count > 0 && _addonDone.Length == 0;

    /// <summary>The second button: "Not now" while there is an offer, "Close" once it has been acted on.</summary>
    public string AddonDismissText => CanUpdateAddon ? "Not now" : "Close";

    /// <summary>What the notice says: the offer, or - once pressed - what the press did.</summary>
    public string AddonOfferText { get; private set; } = "";

    /// <summary>
    /// LOOK, and change nothing: which installed add-ons are older than the one shipped here. Run
    /// at start and again after the Plugins window installs or removes one. The answer is read
    /// from the installed files every time, never remembered.
    /// </summary>
    public void CheckAddon()
    {
        try
        {
            var found = BlenderPlugin.Installs();
            _staleAddons = found.Select(BlenderPlugin.StaleOf).OfType<BlenderPlugin.Stale>().ToList();
            _addonDone = "";

            if (_staleAddons.Count > 0)
            {
                var names = string.Join(", ", _staleAddons.Select(s =>
                    $"Blender {s.Install.Version} ({(s.Installed is null ? "no version" : "add-on " + s.Installed)})"));
                var shipped = _staleAddons[0].Shipped;
                var open = BlenderPlugin.BlenderRunning();

                AddonOfferText =
                    $"The Blender add-on installed for {names} is older than the one this Studio ships ({shipped}). " +
                    // Only an add-on whose backend.json names the old toolkit program is KNOWN to fail
                    // that way; any other older add-on is just older.
                    (_staleAddons.Any(s => s.OldProgram)
                        ? "The old one runs a program this version no longer includes, so importing from Blender fails " +
                          "until it is updated. "
                        : "") +
                    "Updating replaces its folder with the new one and changes nothing else." +
                    // MEASURED (a background Blender 5.1 holding the old add-on while this ran): the
                    // replace goes through with Blender open, the running Blender keeps the OLD code
                    // in memory even after the add-on is switched off and on, and a Blender started
                    // afterwards finds it still switched on and loads the new one.
                    (open
                        ? " Blender is open right now. It can stay open while this runs, but restart it afterwards: it keeps the old add-on in memory until it does, and switching the add-on off and on is not enough."
                        : " Start Blender afterwards; the add-on stays switched on, so there is nothing to tick again.");

                foreach (var s in _staleAddons)
                    Say($"blender add-on: out of date for Blender {s.Install.Version}: {s.Why}");
            }
            else AddonOfferText = "";
        }
        catch (Exception e)
        {
            _staleAddons = [];
            AddonOfferText = "";
            Say("blender add-on: could not look: " + e.Message);
        }

        RaiseAddonOffer();
    }

    private void RaiseAddonOffer()
    {
        Raise(nameof(HasAddonOffer));
        Raise(nameof(CanUpdateAddon));
        Raise(nameof(AddonOfferText));
        Raise(nameof(AddonDismissText));
    }

    /// <summary>"Not now": hide the notice for this run. It asks again at the next start.</summary>
    public void DismissAddonOffer()
    {
        _addonDismissed = true;
        RaiseAddonOffer();
    }

    // ------------------------------------------------------------------ writes

    /// <summary>
    /// Whether this run may WRITE to a Blender's add-on folder. Always, in a release. In a
    /// developer build running in the background (ZCMS_BACKGROUND=1 - every automated test), only
    /// when the Blender root has been pointed at a scratch folder: a test that forgot to do that
    /// would otherwise install, update or remove an add-on in the Blender somebody is using, and
    /// that is refused here whatever called it.
    /// </summary>
    internal static bool AddonWriteAllowed(out string refusal)
    {
        refusal = "";
#if DEV
        if (BackgroundMode.On && BlenderPlugin.RootOverride is null)
        {
            refusal = "this is a background test run and its Blender folder has not been " +
                      "pointed at a scratch folder (ZCMS_BLENDER_ROOT), so it may not change a real Blender's add-ons";
            return false;
        }
#endif
        return true;
    }

    /// <summary>
    /// THE ONE CLICK. Run the installer for every Blender whose add-on is older, say what it did,
    /// and leave the notice standing with the result (and what Blender needs) instead of the
    /// button. Returns true when every one was updated.
    /// </summary>
    public bool UpdateAddon()
    {
        if (!AddonWriteAllowed(out var refusal)) { SayBad("blender add-on: " + refusal); return false; }
        if (_staleAddons.Count == 0) { Say("blender add-on: nothing to update"); return true; }

        var all = true;
        var updated = new List<string>();
        foreach (var s in _staleAddons)
        {
            var why = BlenderPlugin.Apply(s.Install);
            if (why is null)
            {
                Say($"blender add-on: updated for Blender {s.Install.Version}: " +
                    $"{s.Installed ?? "unknown"} -> {s.Install.AddonVersion ?? s.Shipped}, in {s.Install.Target}");
                if (s.Install.BackendNamed is { } backend) Say($"   it now runs {backend}");
                updated.Add(s.Install.Version);
            }
            else
            {
                all = false;
                SayBad($"blender add-on: could not update Blender {s.Install.Version}: {why}");
            }
        }

        var open = BlenderPlugin.BlenderRunning();
        if (all)
        {
            _addonDone = $"The Blender add-on is updated (Blender {string.Join(", ", updated)})." +
                         (open ? " Blender is open: restart it for the new add-on to load (switching the add-on off and on is not enough)."
                               : " It is ready the next time Blender starts, and is still switched on.");
            _staleAddons = [];
            AddonOfferText = _addonDone;
        }
        else
        {
            // THE BUTTON STAYS: what could not be updated is still out of date, and the sentence
            // names the usual reason (a file Blender has open) and the way out.
            _addonDone = "";
            _staleAddons = _staleAddons.Where(x => !updated.Contains(x.Install.Version)).ToList();
            AddonOfferText = "Some of it could not be updated; the log says why. Close Blender and press the button again.";
        }
        // Dismissal is per notice: a new result is worth reading.
        _addonDismissed = false;
        RaiseAddonOffer();
        return all;
    }

    /// <summary>The Plugins window's Install / Reinstall: same installer, same guard, one Blender.</summary>
    public string? InstallAddon(BlenderPlugin.Install into)
    {
        if (!AddonWriteAllowed(out var refusal)) { SayBad("blender add-on: " + refusal); return refusal; }
        var why = BlenderPlugin.Apply(into);
        if (why is null) Say($"Blender add-on installed for Blender {into.Version}");
        else SayBad($"blender add-on: could not install for Blender {into.Version}: {why}");
        CheckAddon();
        return why;
    }

    /// <summary>The Plugins window's Remove.</summary>
    public string? RemoveAddon(BlenderPlugin.Install from)
    {
        if (!AddonWriteAllowed(out var refusal)) { SayBad("blender add-on: " + refusal); return refusal; }
        var why = BlenderPlugin.Remove(from);
        if (why is null) Say($"Blender add-on removed for Blender {from.Version}");
        else SayBad($"blender add-on: could not remove it for Blender {from.Version}: {why}");
        CheckAddon();
        return why;
    }
}
