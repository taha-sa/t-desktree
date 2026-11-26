using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AmberTree.Models;
using Microsoft.Win32.SafeHandles;

namespace AmberTree.Services;

/// <summary>
/// Seviye C: NTFS MFT (Master File Table) doğrudan ikili okuma motoru.
/// WizTree / Everything tarzı çalışan bu motor, diski klasör klasör gezmek yerine
/// NTFS MFT tablosunu doğrudan 4 MB bloklar halinde ham diskten okuyarak tüm diski saniyeler içinde tarar.
/// Yönetici yetkisi gerektirir. Yetki yoksa veya sürücü NTFS değilse otomatik olarak paralel ScanEngine'e döner.
/// </summary>
public class MftScanEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;

    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    [StructLayout(LayoutKind.Sequential)]
    private struct NTFS_VOLUME_DATA_BUFFER
    {
        public ulong VolumeSerialNumber;
        public ulong NumberSectors;
        public ulong TotalClusters;
        public ulong FreeClusters;
        public ulong TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public ulong MftValidDataLength;
        public ulong MftStartLcn;
        public ulong Mft2StartLcn;
        public ulong MftZoneStart;
        public ulong MftZoneEnd;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    public event Action<ScanProgress>? ProgressChanged;

    private readonly Stopwatch _stopwatch = new();
    private long _filesScanned;
    private long _foldersScanned;
    private long _bytesScanned;
    private long _lastProgressMs;

    public static bool IsSupported(string targetPath)
    {
        try
        {
            if (!ShellHelper.IsRunningAsAdministrator()) return false;
            if (string.IsNullOrWhiteSpace(targetPath)) return false;

            var root = Path.GetPathRoot(targetPath);
            if (string.IsNullOrWhiteSpace(root)) return false;

            var drive = new DriveInfo(root);
            return drive.IsReady && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<ScanResult?> TryScanAsync(
        string targetPath,
        CancellationToken ct,
        FsItem? existingRoot = null,
        TreeSortColumn sortColumn = TreeSortColumn.Size,
        bool sortAscending = false)
    {
        if (!IsSupported(targetPath)) return null;

        try
        {
            return await Task.Run(() => PerformMftScan(targetPath, ct, existingRoot, sortColumn, sortAscending), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // MFT erişiminde veya çözümlemesinde herhangi bir hata olursa güvenli şekilde null dönerek klasik motora devreder
            return null;
        }
    }

    private sealed class MftNode
    {
        public ulong RecordId;
        public ulong ParentId;
        public string Name = string.Empty;
        public long Size;
        public long AllocatedSize;
        public bool IsDirectory;
        public FileAttributes Attributes;
        public DateTime LastModified;
        public List<MftNode>? Children;
        public long FileCount;
        public long FolderCount;
    }

    private record struct DataRun(long StartCluster, long ClusterCount);

    private ScanResult? PerformMftScan(
        string targetPath,
        CancellationToken ct,
        FsItem? existingRoot,
        TreeSortColumn sortColumn,
        bool sortAscending)
    {
        _stopwatch.Restart();
        _filesScanned = 0;
        _foldersScanned = 0;
        _bytesScanned = 0;
        _lastProgressMs = 0;

        var rootPath = Path.GetPathRoot(targetPath);
        if (string.IsNullOrWhiteSpace(rootPath)) return null;
        string driveLetter = rootPath.TrimEnd('\\', ':');
        string devicePath = $@"\\.\{driveLetter}:";

        using var hVolume = CreateFile(
            devicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_SEQUENTIAL_SCAN,
            IntPtr.Zero);

        if (hVolume.IsInvalid)
        {
            return null; // Yetki yok ya da açılamadı -> null dönerek standart motora aktar
        }

        // 1. NTFS Volume bilgilerini al
        int volDataSize = Marshal.SizeOf<NTFS_VOLUME_DATA_BUFFER>();
        IntPtr ptrVolData = Marshal.AllocHGlobal(volDataSize);
        NTFS_VOLUME_DATA_BUFFER volData;
        try
        {
            if (!DeviceIoControl(hVolume, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, ptrVolData, (uint)volDataSize, out _, IntPtr.Zero))
            {
                return null;
            }
            volData = Marshal.PtrToStructure<NTFS_VOLUME_DATA_BUFFER>(ptrVolData);
        }
        finally
        {
            Marshal.FreeHGlobal(ptrVolData);
        }

        uint bytesPerCluster = volData.BytesPerCluster;
        if (bytesPerCluster == 0) return null;
        uint recordSize = volData.BytesPerFileRecordSegment >= 48 ? volData.BytesPerFileRecordSegment : 1024;
        long mftByteOffset = (long)volData.MftStartLcn * bytesPerCluster;

        // 2. MFT'nin ilk kaydını (Kayıt 0 - $MFT) oku ve $DATA veri koşularını (data runs) çöz
        byte[] record0 = new byte[recordSize];
        if (!SetFilePointerEx(hVolume, mftByteOffset, out _, 0) || !ReadFile(hVolume, record0, recordSize, out uint r0Read, IntPtr.Zero) || r0Read < recordSize)
        {
            return null;
        }

        List<DataRun> dataRuns = ExtractMftDataRuns(record0);
        if (dataRuns.Count == 0)
        {
            long totalMftBytes = (long)volData.MftValidDataLength;
            if (totalMftBytes <= 0) totalMftBytes = 256 * 1024 * 1024;
            dataRuns.Add(new DataRun((long)volData.MftStartLcn, totalMftBytes / bytesPerCluster));
        }

        // 3. MFT Tablosunu 4 MB bloklar halinde ardışık tara (run başına tek seek)
        int estimatedRecords = (int)Math.Clamp((long)(volData.MftValidDataLength / recordSize), 65_536L, 4_000_000L);
        var nodes = new Dictionary<ulong, MftNode>(estimatedRecords);
        DateTime fallbackNow = DateTime.Now;

        const uint readBlockSize = 4 * 1024 * 1024; // 4 MB bloklar: 8x daha az ReadFile sistem çağrısı
        byte[] readBuffer = new byte[readBlockSize];

        ulong currentRecordIndex = 0;
        foreach (var run in dataRuns)
        {
            if (ct.IsCancellationRequested) break;

            long runByteOffset = run.StartCluster * bytesPerCluster;
            long runByteLength = run.ClusterCount * bytesPerCluster;
            if (!SetFilePointerEx(hVolume, runByteOffset, out _, 0)) break;

            long runBytesRead = 0;
            while (runBytesRead < runByteLength && !ct.IsCancellationRequested)
            {
                uint toRead = (uint)Math.Min(readBlockSize, runByteLength - runBytesRead);
                if (!ReadFile(hVolume, readBuffer, toRead, out uint actuallyRead, IntPtr.Zero) || actuallyRead == 0)
                {
                    break;
                }

                int offset = 0;
                int limit = (int)actuallyRead;
                int recSize = (int)recordSize;
                while (offset + recSize <= limit)
                {
                    ParseRecord(readBuffer, offset, recSize, currentRecordIndex, nodes, bytesPerCluster, fallbackNow);
                    offset += recSize;
                    currentRecordIndex++;
                }

                runBytesRead += actuallyRead;
                ReportProgressThrottled($"{driveLetter}:\\ [MFT: {_filesScanned:N0}]");
            }
        }

        if (ct.IsCancellationRequested)
        {
            return null;
        }

        // 4. Çocukları ebeveynlerine bağla (Parent Linking)
        foreach (var node in nodes.Values)
        {
            if (node.RecordId == 5 || node.RecordId == node.ParentId) continue;

            if (nodes.TryGetValue(node.ParentId, out var parentNode))
            {
                parentNode.Children ??= new List<MftNode>();
                parentNode.Children.Add(node);
            }
        }

        // 5. Boyutları aşağıdan yukarıya doğru topla (Post-order Rollup)
        if (!nodes.TryGetValue(5, out var rootNode))
        {
            return null;
        }

        RollupSizes(rootNode, new HashSet<ulong>());

        // 6. Hedef klasörü bul (Eğer tüm disk değil de alt klasör seçilmişse)
        MftNode targetMftNode = rootNode;
        if (!string.Equals(targetPath.TrimEnd('\\'), rootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            var found = FindSubNode(rootNode, targetPath.Substring(rootPath.Length));
            if (found == null) return null; // Alt klasör MFT'de bulunamadı (ör. bağlama noktası): klasik motora dön
            targetMftNode = found;
        }

        // 7. FsItem hiyerarşisine tek geçişte dönüştür, yüzdeleri hesapla ve sırala
        var dirInfo = new DirectoryInfo(targetPath);
        FsItem rootItem = existingRoot ?? new FsItem
        {
            Name = string.IsNullOrEmpty(dirInfo.Name) ? targetPath : dirInfo.Name,
            FullPath = targetPath,
            IsDirectory = true,
            LastModified = dirInfo.Exists ? dirInfo.LastWriteTime : fallbackNow,
            Attributes = dirInfo.Exists ? dirInfo.Attributes : FileAttributes.Directory,
            PercentOfParent = 100.0,
            PercentOfTotal = 100.0,
            IsExpanded = true
        };

        rootItem.Size = targetMftNode.Size;
        rootItem.AllocatedSize = targetMftNode.AllocatedSize;
        rootItem.FileCount = targetMftNode.FileCount;
        rootItem.FolderCount = targetMftNode.FolderCount;
        rootItem.PercentOfParent = 100.0;
        rootItem.PercentOfTotal = targetMftNode.Size > 0 ? 100.0 : 0.0;

        PopulateAndSortFsItemTree(targetMftNode, rootItem, targetPath, targetMftNode.Size, sortColumn, sortAscending, new HashSet<ulong>());

        long finalFiles = targetMftNode.FileCount;
        long finalFolders = targetMftNode.FolderCount;

        // Geçici MFT düğüm haritasını derhal serbest bırakıp Gen2/LOH sıkıştır (RAM'i minimuma indir)
        nodes.Clear();
        nodes = null!;
        rootNode = null!;
        targetMftNode = null!;
        readBuffer = null!;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        _stopwatch.Stop();

        return new ScanResult
        {
            Root = rootItem,
            Extensions = new List<ExtensionStat>(),
            TopFiles = new List<TopFileItem>(),
            AgeDistribution = new List<AgeDistributionItem>(),
            Elapsed = _stopwatch.Elapsed,
            TotalFiles = finalFiles,
            TotalFolders = finalFolders,
            TotalSize = rootItem.Size,
            TotalAllocated = rootItem.AllocatedSize,
            WasCancelled = ct.IsCancellationRequested
        };
    }

    private static List<DataRun> ExtractMftDataRuns(byte[] record)
    {
        var runs = new List<DataRun>();
        try
        {
            if (record[0] != 'F' || record[1] != 'I' || record[2] != 'L' || record[3] != 'E') return runs;

            int attrOffset = BitConverter.ToUInt16(record, 20);
            while (attrOffset + 8 <= record.Length)
            {
                uint attrType = BitConverter.ToUInt32(record, attrOffset);
                if (attrType == 0xFFFFFFFF || attrType == 0) break;
                uint attrLen = BitConverter.ToUInt32(record, attrOffset + 4);
                if (attrLen < 8 || attrLen > (uint)(record.Length - attrOffset)) break;

                if (attrType == 0x80) // $DATA
                {
                    byte nonResident = record[attrOffset + 8];
                    if (nonResident == 1 && attrOffset + 34 <= record.Length)
                    {
                        ushort runOffset = BitConverter.ToUInt16(record, attrOffset + 32);
                        int pos = attrOffset + runOffset;
                        long prevLcn = 0;

                        while (pos < attrOffset + attrLen && record[pos] != 0)
                        {
                            byte header = record[pos++];
                            int lenBytes = header & 0x0F;
                            int offsetBytes = (header >> 4) & 0x0F;

                            if (lenBytes == 0 || lenBytes > 8 || offsetBytes > 8 || pos + lenBytes + offsetBytes > record.Length) break;

                            long clusterCount = 0;
                            for (int i = 0; i < lenBytes; i++)
                            {
                                clusterCount |= ((long)record[pos++]) << (i * 8);
                            }

                            long lcnDelta = 0;
                            for (int i = 0; i < offsetBytes; i++)
                            {
                                lcnDelta |= ((long)record[pos++]) << (i * 8);
                            }
                            if (offsetBytes > 0 && (record[pos - 1] & 0x80) != 0)
                            {
                                for (int i = offsetBytes; i < 8; i++)
                                {
                                    lcnDelta |= ((long)0xFF) << (i * 8);
                                }
                            }

                            long currentLcn = prevLcn + lcnDelta;
                            prevLcn = currentLcn;

                            if (offsetBytes > 0 && clusterCount > 0 && currentLcn >= 0)
                            {
                                runs.Add(new DataRun(currentLcn, clusterCount));
                            }
                        }
                    }
                    break;
                }
                attrOffset += (int)attrLen;
            }
        }
        catch { }
        return runs;
    }

    private void ParseRecord(
        byte[] buffer,
        int offset,
        int recordSize,
        ulong recordIndex,
        Dictionary<ulong, MftNode> nodes,
        uint clusterSize,
        DateTime fallbackNow)
    {
        if (buffer[offset] != 'F' || buffer[offset + 1] != 'I' || buffer[offset + 2] != 'L' || buffer[offset + 3] != 'E')
        {
            return;
        }

        ushort flags = BitConverter.ToUInt16(buffer, offset + 22);
        if ((flags & 0x01) == 0) return; // Silinmiş kayıt

        bool isDir = (flags & 0x02) != 0;

        // USA Fixup uygula
        ushort usaOffset = BitConverter.ToUInt16(buffer, offset + 4);
        ushort usaCount = BitConverter.ToUInt16(buffer, offset + 6);
        if (usaCount > 1 && usaOffset + usaCount * 2 <= recordSize)
        {
            int sectorCount = usaCount - 1;
            for (int s = 0; s < sectorCount; s++)
            {
                int sectorEndOffset = offset + (s + 1) * 512 - 2;
                int fixupPos = offset + usaOffset + 2 + (s * 2);
                if (sectorEndOffset + 2 <= offset + recordSize && fixupPos + 2 <= offset + recordSize)
                {
                    buffer[sectorEndOffset] = buffer[fixupPos];
                    buffer[sectorEndOffset + 1] = buffer[fixupPos + 1];
                }
            }
        }

        int attrOffset = BitConverter.ToUInt16(buffer, offset + 20);
        string? fileName = null;
        ulong parentRef = 0;
        long fileSize = 0;
        long allocatedSize = 0;
        FileAttributes fileAttrs = isDir ? FileAttributes.Directory : FileAttributes.Normal;
        DateTime lastWriteTime = DateTime.MinValue;

        while (attrOffset + 8 <= recordSize)
        {
            uint attrType = BitConverter.ToUInt32(buffer, offset + attrOffset);
            if (attrType == 0xFFFFFFFF || attrType == 0) break;
            uint attrLen = BitConverter.ToUInt32(buffer, offset + attrOffset + 4);
            if (attrLen < 8 || attrLen > (uint)(recordSize - attrOffset)) break;

            if (attrType == 0x10) // $STANDARD_INFORMATION
            {
                byte nonResident = buffer[offset + attrOffset + 8];
                if (nonResident == 0 && attrOffset + 22 <= recordSize)
                {
                    ushort contentOffset = BitConverter.ToUInt16(buffer, offset + attrOffset + 20);
                    int cPos = offset + attrOffset + contentOffset;
                    if (cPos + 16 <= offset + recordSize)
                    {
                        long ft = BitConverter.ToInt64(buffer, cPos + 8);
                        if (ft > 0)
                        {
                            try { lastWriteTime = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { }
                        }
                    }
                }
            }
            else if (attrType == 0x30) // $FILE_NAME
            {
                byte nonResident = buffer[offset + attrOffset + 8];
                if (nonResident == 0 && attrOffset + 22 <= recordSize)
                {
                    ushort contentOffset = BitConverter.ToUInt16(buffer, offset + attrOffset + 20);
                    int cPos = offset + attrOffset + contentOffset;

                    if (cPos + 66 <= offset + recordSize)
                    {
                        byte namespaceType = buffer[cPos + 65];
                        if (namespaceType != 2 || fileName == null)
                        {
                            parentRef = BitConverter.ToUInt64(buffer, cPos) & 0x0000FFFFFFFFFFFF;
                            long realSize = BitConverter.ToInt64(buffer, cPos + 48);
                            long allocSize = BitConverter.ToInt64(buffer, cPos + 40);
                            uint fAttr = BitConverter.ToUInt32(buffer, cPos + 56);
                            byte nameLen = buffer[cPos + 64];

                            if (lastWriteTime == DateTime.MinValue)
                            {
                                long ft = BitConverter.ToInt64(buffer, cPos + 16);
                                if (ft > 0)
                                {
                                    try { lastWriteTime = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { }
                                }
                            }

                            if (cPos + 66 + nameLen * 2 <= offset + recordSize)
                            {
                                fileName = Encoding.Unicode.GetString(buffer, cPos + 66, nameLen * 2);
                                if (fileSize == 0) fileSize = realSize;
                                if (allocatedSize == 0) allocatedSize = allocSize;
                                fileAttrs = (FileAttributes)fAttr;
                            }
                        }
                    }
                }
            }
            else if (attrType == 0x80 && !isDir) // $DATA (adsız ana akış)
            {
                byte nonResident = buffer[offset + attrOffset + 8];
                byte attrNameLen = buffer[offset + attrOffset + 9];
                if (attrNameLen == 0)
                {
                    if (nonResident == 0 && attrOffset + 20 <= recordSize)
                    {
                        uint dataLen = BitConverter.ToUInt32(buffer, offset + attrOffset + 16);
                        fileSize = dataLen;
                        allocatedSize = NativeFileSystem.AlignToCluster(fileSize, clusterSize);
                    }
                    else if (nonResident != 0 && attrOffset + 56 <= recordSize)
                    {
                        allocatedSize = BitConverter.ToInt64(buffer, offset + attrOffset + 40);
                        fileSize = BitConverter.ToInt64(buffer, offset + attrOffset + 48);
                    }
                }
            }

            attrOffset += (int)attrLen;
        }

        if (string.IsNullOrEmpty(fileName)) return;
        if (fileName.StartsWith('$') && recordIndex < 16) return;

        var node = new MftNode
        {
            RecordId = recordIndex,
            ParentId = parentRef,
            Name = fileName,
            Size = fileSize,
            AllocatedSize = allocatedSize > 0 ? allocatedSize : NativeFileSystem.AlignToCluster(fileSize, clusterSize),
            IsDirectory = isDir,
            Attributes = fileAttrs,
            LastModified = lastWriteTime > DateTime.MinValue ? lastWriteTime : fallbackNow
        };

        nodes[recordIndex] = node;

        if (isDir)
        {
            _foldersScanned++;
        }
        else
        {
            _filesScanned++;
            _bytesScanned += fileSize;
        }
    }

    private static void RollupSizes(MftNode node, HashSet<ulong> visited)
    {
        if (!visited.Add(node.RecordId)) return;
        if (node.Children == null || node.Children.Count == 0) return;

        long totalSize = 0;
        long totalAllocated = 0;
        long totalFiles = 0;
        long totalFolders = 0;

        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (child.IsDirectory)
            {
                RollupSizes(child, visited);
                totalSize += child.Size;
                totalAllocated += child.AllocatedSize;
                totalFiles += child.FileCount;
                totalFolders += 1 + child.FolderCount;
            }
            else
            {
                totalSize += child.Size;
                totalAllocated += child.AllocatedSize;
                totalFiles += 1;
            }
        }

        node.Size = totalSize;
        node.AllocatedSize = totalAllocated;
        node.FileCount = totalFiles;
        node.FolderCount = totalFolders;
    }

    private static MftNode? FindSubNode(MftNode current, string relativePath)
    {
        var parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var target = current;

        foreach (var part in parts)
        {
            if (target.Children == null) return null;
            var match = target.Children.FirstOrDefault(c => c.IsDirectory && string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;
            target = match;
        }

        return target;
    }

    private static void PopulateAndSortFsItemTree(
        MftNode mftNode,
        FsItem fsItem,
        string currentPath,
        long rootTotalSize,
        TreeSortColumn sortColumn,
        bool sortAscending,
        HashSet<ulong> visited)
    {
        if (!visited.Add(mftNode.RecordId)) return;
        if (mftNode.Children == null || mftNode.Children.Count == 0) return;

        long parentSize = mftNode.Size;
        int count = mftNode.Children.Count;
        var children = new FsItem[count];

        for (int i = 0; i < count; i++)
        {
            var child = mftNode.Children[i];
            var item = new FsItem
            {
                Name = child.Name,
                IsDirectory = child.IsDirectory,
                Size = child.Size,
                AllocatedSize = child.AllocatedSize,
                FileCount = child.IsDirectory ? child.FileCount : 1,
                FolderCount = child.FolderCount,
                Attributes = child.Attributes,
                LastModified = child.LastModified,
                Parent = fsItem,
                PercentOfTotal = rootTotalSize > 0 ? (double)child.Size / rootTotalSize * 100.0 : 0.0,
                PercentOfParent = parentSize > 0 ? (double)child.Size / parentSize * 100.0 : 0.0
            };

            if (child.IsDirectory && child.Children != null && child.Children.Count > 0)
            {
                PopulateAndSortFsItemTree(child, item, Path.Combine(currentPath, child.Name), rootTotalSize, sortColumn, sortAscending, visited);
            }
            children[i] = item;
        }

        // Geçici MFT çocuk listesini dönüştürür dönüştürmez serbest bırak
        mftNode.Children = null;

        TreeMaintenance.SortArray(children, currentPath, sortColumn, sortAscending);
        fsItem.SetChildArray(children);
    }

    private void ReportProgressThrottled(string currentPath)
    {
        long now = Environment.TickCount64;
        if (now - _lastProgressMs >= 80)
        {
            _lastProgressMs = now;
            ProgressChanged?.Invoke(new ScanProgress
            {
                CurrentPath = currentPath,
                FilesScanned = _filesScanned,
                FoldersScanned = _foldersScanned,
                BytesScanned = _bytesScanned,
                Elapsed = _stopwatch.Elapsed
            });
        }
    }
}
