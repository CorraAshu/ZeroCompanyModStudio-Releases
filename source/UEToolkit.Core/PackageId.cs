namespace UEToolkit.Core;

/// <summary>
/// The id an IoStore container files a package under.
///
/// A .utoc addresses its contents by chunk id, not by path -- the directory index is a
/// convenience laid over the top. For a package chunk that id IS the package id, so writing a
/// container means computing it, and getting it wrong means the runtime looks in the right
/// container and finds nothing.
///
/// It is CityHash64 over the lowercased UTF-16 package name. That makes it the third use of the
/// same hash in this format, and the three differ only in how the input is prepared:
///
///     <see cref="NameHash"/>          lowercased UTF-8,  no post-processing
///     <see cref="ScriptObjectHash"/>  lowercased UTF-16, '.' and ':' become '/', top 2 bits cleared
///     this one                        lowercased UTF-16, no post-processing
///
/// Confirmed against the mod container's own chunk ids and then against every package in the
/// game; `--chunkid` re-runs that.
/// </summary>
public static class PackageId
{
    public static ulong Of(string packageName)
    {
        var chars = packageName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            // ASCII-only, matching FChar::ToLower. Separators are left alone here, unlike the
            // script object hash -- that difference is not cosmetic, it changes every result.
            if (c is >= 'A' and <= 'Z') chars[i] = (char) (c + 32);
        }
        return NameHash.CityHash64(System.Text.Encoding.Unicode.GetBytes(chars));
    }

    /// <summary>
    /// The `/Game/...` package name for a mounted file path, or null when the path is not a
    /// package under a content root.
    ///
    /// A mount root is `&lt;Something&gt;/Content/`, and the package name is `/&lt;Name&gt;/rest`
    /// where Name is the LAST segment before /Content/ -- not the whole prefix. A plugin at
    /// `Engine/Plugins/Media/MediaPlate/Content/` mounts as `/MediaPlate/`, and using the full
    /// path instead put 562 of the game's packages under names that hash to nothing.
    ///
    /// One exception is not derivable and has to be known: the PROJECT's own content mounts as
    /// `/Game/`, never as a root named after the project.
    /// </summary>
    public static string? PackageNameFor(string mountedPath, string projectName)
    {
        var dot = mountedPath.LastIndexOf('.');
        if (dot < 0) return null;
        var noExt = mountedPath[..dot].Replace('\\', '/');

        const string marker = "/Content/";
        var at = noExt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var prefix = noExt[..at];
        var rest = noExt[(at + marker.Length)..];
        if (string.Equals(prefix, projectName, StringComparison.OrdinalIgnoreCase))
            return "/Game/" + rest;

        var slash = prefix.LastIndexOf('/');
        var root = slash < 0 ? prefix : prefix[(slash + 1)..];
        return "/" + root + "/" + rest;
    }
}
