using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace FileTabs;

public sealed class EverythingSearchResult
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsFolder { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? Modified { get; init; }

    public string SizeText => IsFolder ? "文件夹" : FormatSize(Size);

    public string ModifiedText => Modified?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";

    private static string FormatSize(long size)
    {
        if (size < 1024) return $"{size} B";
        if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
        if (size < 1024L * 1024 * 1024) return $"{size / 1024.0 / 1024:F1} MB";
        return $"{size / 1024.0 / 1024 / 1024:F2} GB";
    }
}

/// <summary>
/// Thin wrapper around the official Everything SDK DLL.
/// All calls are serialized because the native SDK keeps global query state.
/// </summary>
public static class EverythingClient
{
    private const uint RequestFileName = 0x00000001;
    private const uint RequestPath = 0x00000002;
    private const uint RequestSize = 0x00000010;
    private const uint RequestDateModified = 0x00000040;
    private const uint SortDateModifiedDescending = 14;
    private const uint SortNameAscending = 1;
    // Values come from the Everything SDK header. The order is
    // name, path, size, extension - each ascending then descending -
    // so size descending is 6 and path ascending is 3.
    private const uint SortSizeDescending = 6;
    private const uint SortPathAscending = 3;

    public enum SortMode
    {
        ModifiedDescending,
        NameAscending,
        SizeDescending,
        PathAscending,
    }

    private const int ErrorIpc = 2;

    private static readonly object Gate = new();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void Everything_SetSearchW(string value);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    private static extern void Everything_SetMatchPath(bool value);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    private static extern void Everything_SetMax(uint value);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    private static extern void Everything_SetOffset(uint value);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    private static extern void Everything_SetRequestFlags(uint value);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    private static extern void Everything_SetSort(uint value);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Everything_QueryW(
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", ExactSpelling = true)]
    private static extern uint Everything_GetLastError();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr Everything_GetResultFileNameW(uint index);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr Everything_GetResultPathW(uint index);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Everything_IsFolderResult(uint index);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Everything_GetResultSize(
        uint index, out long size);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Everything_GetResultDateModified(
        uint index, out long fileTime);

    [DllImport("Everything64.dll", ExactSpelling = true)]
    private static extern void Everything_CleanUp();

    public static bool IsAvailable()
    {
        lock (Gate)
        {
            try
            {
                Everything_SetSearchW("");
                Everything_SetRequestFlags(RequestFileName);
                Everything_SetMax(1);
                Everything_SetOffset(0);
                Everything_SetSort(1);
                Everything_SetMatchPath(false);
                return Everything_QueryW(true);
            }
            finally
            {
                Everything_CleanUp();
            }
        }
    }

    public static List<EverythingSearchResult> Search(
        string query, int maxResults = 50, int offset = 0, bool matchPath = false,
        SortMode sort = SortMode.ModifiedDescending)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<EverythingSearchResult>();
        query = query.Trim();

        lock (Gate)
        {
            try
            {
                Everything_SetSearchW(query);
                Everything_SetMatchPath(matchPath);
                Everything_SetRequestFlags(
                    RequestFileName | RequestPath | RequestSize | RequestDateModified);
                Everything_SetSort(sort switch
                {
                    SortMode.NameAscending => SortNameAscending,
                    SortMode.SizeDescending => SortSizeDescending,
                    SortMode.PathAscending => SortPathAscending,
                    _ => SortDateModifiedDescending,
                });
                Everything_SetMax((uint)Math.Max(1, maxResults));
                Everything_SetOffset((uint)Math.Max(0, offset));

                if (!Everything_QueryW(true))
                {
                    throw new InvalidOperationException(GetErrorMessage(Everything_GetLastError()));
                }

                var results = new List<EverythingSearchResult>((int)Everything_GetNumResults());
                var folder = (uint)0;
                for (; folder < Everything_GetNumResults(); folder++)
                {
                    var name = ReadNativeString(Everything_GetResultFileNameW(folder));
                    var path = ReadNativeString(Everything_GetResultPathW(folder));
                    var size = 0L;
                    var hasSize = Everything_GetResultSize(folder, out size);
                    DateTimeOffset? modified = null;
                    if (Everything_GetResultDateModified(folder, out var localModified))
                    {
                        modified = DateTimeOffset.FromFileTime(localModified);
                    }

                    results.Add(new EverythingSearchResult
                    {
                        Name = name,
                        Path = path,
                        FullPath = path.Length == 0 ? name : Path.Combine(path, name),
                        IsFolder = Everything_IsFolderResult(folder),
                        Size = hasSize ? size : 0,
                        Modified = modified,
                    });
                }

                return results;
            }
            finally
            {
                Everything_CleanUp();
            }
        }
    }

    private static string ReadNativeString(IntPtr pointer)
        => pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer) ?? string.Empty;

    private static string GetErrorMessage(uint code) => code switch
    {
        ErrorIpc => "Everything is not running or IPC is unavailable.",
        _ => $"Everything SDK error {code}.",
    };
}




