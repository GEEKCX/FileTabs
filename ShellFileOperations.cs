using System.IO;
using System.Runtime.InteropServices;

namespace FileTabs;

/// <summary>
/// Copy/delete through IFileOperation, the shell API Explorer itself uses.
/// Doing the work in the shell keeps overwrite prompts, the progress dialog,
/// the Recycle Bin path and Ctrl+Z undo identical to a native window; plain
/// File.Delete would bypass all of that.
/// </summary>
public static class ShellFileOperations
{
    // FOF_ALLOWUNDO keeps deletes recoverable; FOF_NOCONFIRMMKDIR skips the
    // "destination folder does not exist, create it?" prompt.
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    // FOFX_RECYCLEONDELETE (Windows 8+) forces the Recycle Bin instead of a
    // permanent delete for items the shell would otherwise unlink directly.
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;

    private static readonly Guid FileOperationClsid =
        new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public static void Copy(IReadOnlyList<string> sources, string destinationFolder)
        => Run(sources, destinationFolder, delete: false);

    public static void DeleteToRecycleBin(IReadOnlyList<string> sources)
        => Run(sources, null, delete: true);

    /// <summary>
    /// Rename a single shell item. Going through IFileOperation instead of
    /// File.Move keeps collision prompts, the undo stack and shell change
    /// notifications identical to a native Explorer rename.
    /// </summary>
    public static void Rename(string source, string newName)
    {
        var operation = (IFileOperation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(FileOperationClsid)!)!;
        try
        {
            ThrowIfFailed(operation.SetOperationFlags(FOF_ALLOWUNDO));
            ThrowIfFailed(operation.SetOwnerWindow(GetActiveWindow()));
            var item = CreateItem(source);
            try
            {
                ThrowIfFailed(operation.RenameItem(item, newName, IntPtr.Zero));
                ThrowIfFailed(operation.PerformOperations());
                ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
                if (aborted) throw new OperationCanceledException();
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(operation);
        }
    }

    private static void Run(IReadOnlyList<string> sources, string? destinationFolder,
        bool delete)
    {
        if (sources.Count == 0) return;

        var operation = (IFileOperation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(FileOperationClsid)!)!;
        try
        {
            var flags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR;
            if (delete) flags |= FOFX_RECYCLEONDELETE;
            ThrowIfFailed(operation.SetOperationFlags(flags));
            ThrowIfFailed(operation.SetOwnerWindow(GetActiveWindow()));

            var destination = destinationFolder is null ? null : CreateItem(destinationFolder);
            try
            {
                foreach (var source in sources)
                {
                    var item = CreateItem(source);
                    try
                    {
                        // Skipping the omitted "advise" sink (null) is valid:
                        // the shell then uses its standard progress UI.
                        ThrowIfFailed(delete
                            ? operation.DeleteItem(item, IntPtr.Zero)
                            : operation.CopyItem(item, destination!, null, IntPtr.Zero));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }

                ThrowIfFailed(operation.PerformOperations());
                ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
                // A dismissed overwrite prompt is a normal outcome, not a
                // crash: the caller must not claim success.
                if (aborted) throw new OperationCanceledException();
            }
            finally
            {
                if (destination is not null) Marshal.ReleaseComObject(destination);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(operation);
        }
    }

    private static void ThrowIfFailed(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static IShellItem CreateItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        ThrowIfFailed(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
        return item!;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, ref Guid riid, out IShellItem? shellItem);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    // Vtable order matches shobjidl_core.h: every slot must be declared even
    // when unused, or later calls land on the wrong method.
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid bhid, ref Guid riid,
            out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint nameType, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr progressDialog);
        [PreserveSig] int SetProperties(IntPtr propertyChangeArray);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item,
            [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination,
            [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint fileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string templateName, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
