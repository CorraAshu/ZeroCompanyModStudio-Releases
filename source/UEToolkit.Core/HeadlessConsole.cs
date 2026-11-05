using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UEToolkit.Core;

/// <summary>
/// MAKE <see cref="Console"/> REACH WHOEVER STARTED A WINDOWS-SUBSYSTEM PROGRAM, for the verbs that answer without a window
/// (the Mod Studio's Blender add-on verbs and the Level Editor's --version and --check). One copy: the Studio's
/// AddonVerbs and the Level Editor's LevelEditorVerbs both call <see cref="Attach"/>.
///
/// A Windows-subsystem exe has no console, and .NET then writes to nowhere unless the parent gave it a handle. Two cases
/// matter:
///   * REDIRECTED (a pipe or a file: Python's capture_output, `> out.txt`, a script's capture). The handle is there and
///     the bytes go through it. They are written as UTF-8 WITHOUT trusting a code page, because no console exists to ask
///     for one - the add-on decodes it as UTF-8.
///   * TYPED AT A CONSOLE. No handle, but the parent has a console: attach to it (never allocate one - that would be a
///     window) and write in its code page. The shell does not wait for a Windows-subsystem program, so its prompt can
///     come back before the last line.
/// When there is neither, output is dropped, which is all there is to do.
/// </summary>
public static class HeadlessConsole
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
                                             uint disposition, uint flags, IntPtr template);

    private const int StdOutput = -11;
    private const int AttachParent = -1;
    private static readonly IntPtr NoHandle = new(-1);

    /// <summary>Make <see cref="Console"/> reach whoever started this process; the returned action (null when there is
    /// nothing to undo) hands the parent's console back once the last line is written.</summary>
    public static Action? Attach()
    {
        var handle = GetStdHandle(StdOutput);
        if (handle != IntPtr.Zero && handle != NoHandle)
        {
            var utf8 = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(utf8);
            Console.SetError(utf8);
            return null;
        }

        if (!AttachConsole(AttachParent)) return null;
        try
        {
            // 0x40000000 GENERIC_WRITE, 3 = FILE_SHARE_READ|WRITE, 3 = OPEN_EXISTING
            var conout = CreateFileW("CONOUT$", 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (conout == NoHandle || conout == IntPtr.Zero) { FreeConsole(); return null; }
            var stream = new FileStream(new SafeFileHandle(conout, ownsHandle: true), FileAccess.Write);
            var encoding = Console.OutputEncoding is UTF8Encoding ? new UTF8Encoding(false) : Console.OutputEncoding;
            var writer = new StreamWriter(stream, encoding) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
            return () => { writer.Flush(); FreeConsole(); };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
