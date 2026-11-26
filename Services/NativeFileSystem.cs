using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace AmberTree.Services;

public static class NativeFileSystem
{
    private static readonly ConcurrentDictionary<string, uint> _clusterSizeCache = new(StringComparer.OrdinalIgnoreCase);
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FIND_FIRST_EX_LARGE_FETCH = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindFirstFileEx(
        string lpFileName,
        int fInfoLevelId,
        out WIN32_FIND_DATA lpFindFileData,
        int fSearchOp,
        IntPtr lpSearchFilter,
        int dwAdditionalFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetCompressedFileSize(string lpFileName, out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    // Recycle bin support via SHFileOperation
    private const int FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    public static bool MoveToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0) return false;
        try
        {
            var shf = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + '\0' + '\0',
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };
            return SHFileOperation(ref shf) == 0 && !shf.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    public static uint GetClusterSize(string rootPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return 4096;
            var root = Path.GetPathRoot(rootPath);
            if (string.IsNullOrWhiteSpace(root)) return 4096;

            return _clusterSizeCache.GetOrAdd(root, r =>
            {
                if (GetDiskFreeSpace(r, out uint sectors, out uint bytes, out _, out _))
                {
                    uint clusterSize = sectors * bytes;
                    return clusterSize > 0 ? clusterSize : 4096;
                }
                return 4096;
            });
        }
        catch { }
        return 4096;
    }

    public static long AlignToCluster(long size, uint clusterSize)
    {
        if (size <= 0) return 0;
        if (clusterSize == 0) clusterSize = 4096;
        return ((size + clusterSize - 1) / clusterSize) * clusterSize;
    }

    public static DateTime FileTimeToDateTime(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        long fileTime = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
