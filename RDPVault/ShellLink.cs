using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RDPVault;

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkCoClass { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
 Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
    void Resolve(IntPtr hwnd, int fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
 Guid("0000010B-0000-0000-C000-000000000046")]
internal interface IPersistFileLink
{
    void GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
}

/// <summary>
/// ISSUE #16 - replaces the old `dynamic shell = Activator.CreateInstance(WScript.Shell)`
/// shortcut creator.
///
/// `dynamic` needs the C# runtime binder, which does not survive trimming or
/// NativeAOT, and the whole call site sat inside `catch { }` - so on a trimmed build
/// the installer created no shortcuts at all and still reported
/// "Installation completed successfully".
///
/// This is a direct IShellLink/IPersistFile implementation: no runtime binder, no
/// Windows Script Host dependency, and failures are thrown rather than swallowed.
/// The COM work runs on a dedicated STA thread because the installer itself runs on
/// a background (MTA) thread.
/// </summary>
internal static class ShellLink
{
    public static void Create(string shortcutPath, string targetPath, string arguments,
                              string description, string? iconPath = null)
    {
        Exception? failure = null;

        var t = new Thread(() =>
        {
            object? comObject = null;
            try
            {
                comObject = new ShellLinkCoClass();
                var link = (IShellLinkW)comObject;
                link.SetPath(targetPath);
                link.SetArguments(arguments ?? "");
                link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(targetPath) ?? "");
                link.SetDescription(description.Length > 200 ? description.Substring(0, 200) : description);
                link.SetIconLocation(iconPath ?? targetPath, 0);
                ((IPersistFileLink)link).Save(shortcutPath, true);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (comObject != null)
                {
                    try { Marshal.FinalReleaseComObject(comObject); } catch { }
                }
            }
        });

        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();

        if (failure != null) throw failure;
    }
}
