using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THE LEVEL EDITOR DOES NOT LOOK FOR UPDATES - the Mod Studio, which ships in the same folder and
/// at the same version, has the one check. This is the answer the LINKED About window (the Studio's
/// own file) asks for, and it is "nothing, always": there is no request in this program to make,
/// and no path to one, because the class the Studio uses for it is deliberately not linked.
///
/// The Studio's UpdateNotice holds a static "found" release, an event raised from the network
/// thread, and the check itself. About reads three members of it - the version that is running,
/// the newer release if one was found, and a change event - so those three exist here and nothing
/// else does. About also hides its "look for a newer version" switch when it is given no settings,
/// which is how the Level Editor opens it, so a person is never offered a switch that does nothing.
/// </summary>
internal static class UpdateNotice
{
    /// <summary>The version this build is: <see cref="Branding.Version"/>.</summary>
    public static string Running => Branding.Version;

    /// <summary>Always null here: nothing ever asks.</summary>
    public static UpdateCheck.Release? Found => null;

    /// <summary>Never raised here; the event exists because About subscribes to it.</summary>
    public static event Action? Changed { add { } remove { } }
}
