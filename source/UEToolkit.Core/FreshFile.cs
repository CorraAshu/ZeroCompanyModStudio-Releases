namespace UEToolkit.Core;

/// <summary>
/// Reading, writing and moving a file THIS PROCESS JUST WROTE, through a transient sharing
/// violation rather than into one.
///
/// A file that has only just been written is still held for a moment -- by the writer's own handle
/// as the OS drains it, by the indexer or virus scanner that wakes on a new file, or by SMB
/// write-behind when the output is a mapped drive. Windows reports that as
///
///     The process cannot access the file '...' because it is being used by another process.
///
/// which reads like a permission problem and is not one. It is a timing window, and the proof that
/// it is a window rather than a refusal is that it is INTERMITTENT at a rate that tracks batch
/// size: moving just-written meshes lost the race on 18 of 566 in one run and 5 in another.
///
/// THE EXPORT ALREADY KNEW THIS AND ONLY HALF-ACTED ON IT. `MirrorGameTree` has retried the MOVE
/// since that measurement, with a comment saying exactly the above -- but the reads that follow it
/// a few lines later did not, and there is nothing about a move that makes it more exposed than
/// the read of the same file one step afterwards.
///
/// Reported 2026-09-10 by Gugarion, exporting the Engine folder to `Z:\...` -- a mapped drive,
/// which is the configuration most likely to show it:
///
///     Export folder Engine: 16 exported, 1 failed
///         SM_CraneRig_Mount: atlas lookup The process cannot access the file
///         '...\SM_CraneRig_Mount.pskx' because it is being used by another process.
///
/// One mesh of seventeen, on a fresh export, with nothing wrong with the mesh.
///
/// WHAT IS NOT RETRIED MATTERS AS MUCH. A missing file and a missing directory are both
/// IOException in .NET, and retrying either would turn an instant, accurate error into the same
/// error four hundred milliseconds later. They rethrow at once. An UnauthorizedAccessException is
/// not an IOException at all, so a genuine permission problem is never caught here.
/// </summary>
public static class FreshFile
{
    /// <summary>
    /// Attempts, and the wait before each retry. 40, 80, 120, 160 -- four hundred milliseconds in
    /// total before giving up, which is long against the window measured here and short against a
    /// batch that has thousands of meshes still to write.
    /// </summary>
    private const int Attempts = 5;

    public static byte[] ReadAllBytes(string path) => Do(() => File.ReadAllBytes(path));

    public static void WriteAllBytes(string path, byte[] bytes) =>
        Do<object?>(() => { File.WriteAllBytes(path, bytes); return null; });

    /// <summary>
    /// The caller owns the stream and must dispose it -- this only survives the opening.
    /// </summary>
    public static FileStream OpenRead(string path) => Do(() => File.OpenRead(path));

    public static void Move(string from, string to, bool overwrite) =>
        Do<object?>(() => { File.Move(from, to, overwrite); return null; });

    private static T Do<T>(Func<T> act)
    {
        IOException? last = null;
        var waited = 0;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try { return act(); }
            // Not transient, and both of these ARE IOExceptions -- so they have to be named before
            // the catch below, or a typo in a path costs four hundred milliseconds and comes back
            // saying the same thing.
            catch (FileNotFoundException) { throw; }
            catch (DirectoryNotFoundException) { throw; }
            catch (IOException e)
            {
                last = e;
                if (attempt < Attempts - 1) { var ms = 40 * (attempt + 1); Thread.Sleep(ms); waited += ms; }
            }
        }

        // SAY THAT WE WAITED, because otherwise the next report of this is indistinguishable from
        // the one before the retry existed -- the same sentence from Windows, and no way to tell
        // whether the lock lasted a millisecond or the whole export. "Still held after 400 ms" is
        // the difference between "a scanner touched it" and "you have it open in another
        // program", and it is the reader who knows which.
        throw new IOException(
            $"{last!.Message} (still held after {Attempts} attempt(s) over {waited} ms -- if this " +
            "keeps happening, something else has the file open: a model viewer, an unzip, or a " +
            "sync client on the output folder)", last);
    }
}
