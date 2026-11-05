using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CUE4Parse.FileProvider;
using UEToolkit.Core;

namespace ZeroCompanyModStudioAvalonia;

/// <summary>
/// THUMBNAILS, ONE ROW AT A TIME, AND ONLY FOR ROWS SOMEBODY IS LOOKING AT.
///
/// 1,178 wearable parts and 1,291 icons in the catalogue. Decoding them up front would be a
/// minute of work to show the twenty rows that fit on screen, so nothing is decoded until a row
/// asks — which, because Avalonia virtualises a ListBox, means when the row is actually realised.
/// Scrolling asks for more; scrolling back asks the cache.
///
/// THE CACHE HOLDS NULLS TOO. A part whose icon cannot be decoded would otherwise be retried
/// every time it scrolled past, which is the same failure repeated rather than remembered.
/// </summary>
public static class IconLoader
{
    /// <summary>Set once the game is mounted. Nothing loads before that, and nothing throws.</summary>
    public static IFileProvider? Provider;

    private static readonly Dictionary<string, Bitmap?> Done = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    /// <summary>
    /// At most two at once.
    ///
    /// Not a throughput choice — the decode's payload fetch is serialised on AssetLoader.Gate
    /// anyway, so more threads would queue on the same lock while taking turns away from the
    /// property panel and the section loaders, which want that gate too.
    /// </summary>
    private static readonly SemaphoreSlim Slots = new(2);

    /// <summary>
    /// Ask for one, and be called back on the UI thread when it is there.
    ///
    /// <paramref name="width"/> is the decoded width, and it is part of the CACHE KEY: a list row
    /// wants twenty pixels and the picker's grid wants eighty, and handing the row's 48-pixel
    /// copy to a grid tile is how a picker for pictures ends up showing mush.
    /// </summary>
    public static void Want(string objectPath, Action<Bitmap?> ready, int width = 48)
    {
        if (objectPath.Length == 0) { ready(null); return; }

        var key = width == 48 ? objectPath : $"{objectPath}@{width}";

        lock (Lock)
        {
            if (Done.TryGetValue(key, out var have)) { ready(have); return; }
            // Two rows can share an icon. Letting both start it decodes the same texture twice.
            if (!InFlight.Add(key)) return;
        }

        _ = Task.Run(async () =>
        {
            await Slots.WaitAsync();
            Bitmap? bitmap = null;
            try
            {
                if (Provider is { } p && Thumbnail.Png(p, objectPath, out _) is { } png)
                {
                    using var stream = new MemoryStream(png);
                    // DECODED TO A FIXED WIDTH, not to the texture's own. A part icon can be
                    // 512 square and the row shows it at twenty; holding the full bitmap for
                    // every row scrolled past is how a list quietly takes a gigabyte.
                    bitmap = Bitmap.DecodeToWidth(stream, width);
                }
            }
            catch (Exception) { /* a missing picture is not worth taking the list down for */ }
            finally { Slots.Release(); }

            lock (Lock) { Done[key] = bitmap; InFlight.Remove(key); }
            await Dispatcher.UIThread.InvokeAsync(() => ready(bitmap));
        });
    }
}
