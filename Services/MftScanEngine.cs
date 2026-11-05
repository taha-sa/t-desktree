using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
/// NTFS MFT tablosunu doğrudan ham disk bloklarından okuyarak tüm diski 1-2 saniyede tarar.
/// Yönetici yetkisi gerektirir. Yetki yoksa veya sürücü NTFS değilse otomatik olarak ScanEngine'e döner.
/// </summary>
public class MftScanEngine
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

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
    private DateTime _lastProgressReport = DateTime.MinValue;

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

    public async Task<ScanResult?> TryScanAsync(string targetPath, CancellationToken ct, FsItem? existingRoot = null)
    {
        if (!IsSupported(targetPath)) return null;

        try
        {
            return await Task.Run(() => PerformMftScan(targetPath, ct, existingRoot), ct);
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

    private class MftNode
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

    private ScanResult? PerformMftScan(string targetPath, CancellationToken ct, FsItem? existingRoot)
    {
        _stopwatch.Restart();
        _filesScanned = 0;
        _foldersScanned = 0;
        _bytesScanned = 0;
        _lastProgressReport = DateTime.MinValue;

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
            FILE_FLAG_BACKUP_SEMANTICS,
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
        uint recordSize = volData.BytesPerFileRecordSegment > 0 ? volData.BytesPerFileRecordSegment : 1024;
        long mftByteOffset = (long)volData.MftStartLcn * bytesPerCluster;

        // 2. MFT'nin ilk kaydını (Kayıt 0 - $MFT) oku ve $DATA veri koşularını (data runs) çöz
        byte[] record0 = new byte[recordSize];
        if (!ReadVolumeBytes(hVolume, mftByteOffset, record0))
        {
            return null;
        }

        List<DataRun> dataRuns = ExtractMftDataRuns(record0);
        if (dataRuns.Count == 0)
        {
            // Veri koşusu bulunamazsa temel başlangıç kümesinden tek parça olarak okumayı dene
            long totalMftBytes = (long)volData.MftValidDataLength;
            if (totalMftBytes <= 0) totalMftBytes = 256 * 1024 * 1024; // varsayılan 256MB
            dataRuns.Add(new DataRun((long)volData.MftStartLcn, totalMftBytes / bytesPerCluster));
        }

        // 3. MFT Tablosunu bloklar halinde tara ve kayıtları belleğe aktar
        var nodes = new Dictionary<ulong, MftNode>(250000);
        var topFilesQueue = new PriorityQueue<TopFileItem, long>();
        object topFilesLock = new();

        const uint readBlockSize = 512 * 1024; // 512 KB bloklar halinde hızlı okuma
        byte[] readBuffer = new byte[readBlockSize];

        ulong currentRecordIndex = 0;
        foreach (var run in dataRuns)
        {
            if (ct.IsCancellationRequested) break;

            long runByteOffset = run.StartCluster * bytesPerCluster;
            long runByteLength = run.ClusterCount * bytesPerCluster;
            long runBytesRead = 0;

            while (runBytesRead < runByteLength && !ct.IsCancellationRequested)
            {
                uint toRead = (uint)Math.Min(readBlockSize, runByteLength - runBytesRead);
                if (!ReadVolumeBytes(hVolume, runByteOffset + runBytesRead, readBuffer, toRead))
                {
                    break;
                }

                // Buffer içindeki 1024 baytlık kayıtları ayrıştır
                int offset = 0;
                while (offset + recordSize <= toRead)
                {
                    ParseRecord(readBuffer, offset, (int)recordSize, currentRecordIndex, nodes, topFilesQueue, topFilesLock, bytesPerCluster);
                    offset += (int)recordSize;
                    currentRecordIndex++;
                }

                runBytesRead += toRead;

                if ((_filesScanned & 2047) == 0)
                {
                    ReportProgressThrottled($"{driveLetter}:\\ [MFT Analizi: {_filesScanned:N0} dosya]");
                }
            }
        }

        if (ct.IsCancellationRequested)
        {
            return null;
        }

        // 4. Çocukları ebeveynlerine bağla (Parent Linking)
        foreach (var node in nodes.Values)
        {
            if (node.RecordId == 5) continue; // Root ($Root)

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

        RollupSizes(rootNode);

        // 6. Hedef klasörü bul (Eğer tüm disk değil de alt klasör seçilmişse)
        MftNode targetMftNode = rootNode;
        if (!string.Equals(targetPath.TrimEnd('\\'), rootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            targetMftNode = FindSubNode(rootNode, targetPath.Substring(rootPath.Length)) ?? rootNode;
        }

        // 7. FsItem hiyerarşisine dönüştür
        var dirInfo = new DirectoryInfo(targetPath);
        FsItem rootItem = existingRoot ?? new FsItem
        {
            Name = string.IsNullOrEmpty(dirInfo.Name) ? targetPath : dirInfo.Name,
            FullPath = targetPath,
            IsDirectory = true,
            LastModified = dirInfo.Exists ? dirInfo.LastWriteTime : DateTime.Now,
            Attributes = dirInfo.Exists ? dirInfo.Attributes : FileAttributes.Directory,
            PercentOfParent = 100.0,
            PercentOfTotal = 100.0,
            IsExpanded = true
        };

        PopulateFsItemTree(targetMftNode, rootItem, targetPath);
        CalculatePercentagesAndSort(rootItem, rootItem.Size);

        _stopwatch.Stop();

        // Top 100 Listesini oluştur
        var topList = new List<TopFileItem>(topFilesQueue.Count);
        while (topFilesQueue.Count > 0)
        {
            topList.Add(topFilesQueue.Dequeue());
        }
        topList.Reverse();
        for (int idx = 0; idx < topList.Count; idx++)
        {
            topList[idx].Rank = idx + 1;
            topList[idx].PercentOfTotal = rootItem.Size > 0 ? (double)topList[idx].Size / rootItem.Size * 100.0 : 0.0;
        }

        return new ScanResult
        {
            Root = rootItem,
            Extensions = new List<ExtensionStat>(),
            TopFiles = topList,
            AgeDistribution = new List<AgeDistributionItem>(),
            Elapsed = _stopwatch.Elapsed,
            TotalFiles = _filesScanned,
            TotalFolders = _foldersScanned,
            TotalSize = rootItem.Size,
            TotalAllocated = rootItem.AllocatedSize,
            WasCancelled = ct.IsCancellationRequested
        };
    }

    private static bool ReadVolumeBytes(SafeFileHandle hVolume, long byteOffset, byte[] buffer, uint? count = null)
    {
        if (!SetFilePointerEx(hVolume, byteOffset, out _, 0)) // FILE_BEGIN
        {
            return false;
        }
        uint toRead = count ?? (uint)buffer.Length;
        return ReadFile(hVolume, buffer, toRead, out _, IntPtr.Zero);
    }

    private static List<DataRun> ExtractMftDataRuns(byte[] record)
    {
        var runs = new List<DataRun>();
        try
        {
            if (record[0] != 'F' || record[1] != 'I' || record[2] != 'L' || record[3] != 'E') return runs;

            ushort attrOffset = BitConverter.ToUInt16(record, 20);
            while (attrOffset + 8 <= record.Length)
            {
                uint attrType = BitConverter.ToUInt32(record, attrOffset);
                if (attrType == 0xFFFFFFFF || attrType == 0) break;
                uint attrLen = BitConverter.ToUInt32(record, attrOffset + 4);
                if (attrLen == 0 || attrOffset + attrLen > record.Length) break;

                if (attrType == 0x80) // $DATA
                {
                    byte nonResident = record[attrOffset + 8];
                    if (nonResident == 1)
                    {
                        ushort runOffset = BitConverter.ToUInt16(record, attrOffset + 32);
                        int pos = attrOffset + runOffset;
                        long prevLcn = 0;

                        while (pos < attrOffset + attrLen && record[pos] != 0)
                        {
                            byte header = record[pos++];
                            int lenBytes = header & 0x0F;
                            int offsetBytes = (header >> 4) & 0x0F;

                            if (lenBytes == 0 || pos + lenBytes + offsetBytes > record.Length) break;

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
                            // Sign extend if highest bit is set
                            if (offsetBytes > 0 && (record[pos - 1] & 0x80) != 0)
                            {
                                for (int i = offsetBytes; i < 8; i++)
                                {
                                    lcnDelta |= ((long)0xFF) << (i * 8);
                                }
                            }

                            long currentLcn = prevLcn + lcnDelta;
                            prevLcn = currentLcn;

                            runs.Add(new DataRun(currentLcn, clusterCount));
                        }
                    }
                    break;
                }
                attrOffset += (ushort)attrLen;
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
        PriorityQueue<TopFileItem, long> topFilesQueue,
        object topFilesLock,
        uint clusterSize)
    {
        // 'FILE' imza kontrolü
        if (buffer[offset] != 'F' || buffer[offset + 1] != 'I' || buffer[offset + 2] != 'L' || buffer[offset + 3] != 'E')
        {
            return;
        }

        ushort flags = BitConverter.ToUInt16(buffer, offset + 22);
        if ((flags & 0x01) == 0) return; // Kayıt kullanımda değil (silinmiş dosya)

        bool isDir = (flags & 0x02) != 0;

        // USA Fixup uygula (sektör sonu 2 baytlarını güncelleme dizisiyle onar)
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

        // Nitelikleri (Attributes) tara
        ushort attrOffset = BitConverter.ToUInt16(buffer, offset + 20);
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
            if (attrLen == 0 || attrOffset + attrLen > recordSize) break;

            if (attrType == 0x30) // $FILE_NAME
            {
                byte nonResident = buffer[offset + attrOffset + 8];
                if (nonResident == 0) // $FILE_NAME her zaman resident'tır
                {
                    ushort contentOffset = BitConverter.ToUInt16(buffer, offset + attrOffset + 20);
                    int cPos = offset + attrOffset + contentOffset;

                    if (cPos + 66 <= offset + recordSize)
                    {
                        byte namespaceType = buffer[cPos + 65];
                        // DOS (8.3) takma adlarını atla (Win32 veya Win32&DOS isimlerini al)
                        if (namespaceType != 2 || fileName == null)
                        {
                            parentRef = BitConverter.ToUInt64(buffer, cPos) & 0x0000FFFFFFFFFFFF;
                            long realSize = BitConverter.ToInt64(buffer, cPos + 48);
                            long allocSize = BitConverter.ToInt64(buffer, cPos + 40);
                            uint fAttr = BitConverter.ToUInt32(buffer, cPos + 56);
                            byte nameLen = buffer[cPos + 64];

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
            else if (attrType == 0x80 && !isDir) // $DATA
            {
                byte nonResident = buffer[offset + attrOffset + 8];
                if (nonResident == 0)
                {
                    uint dataLen = BitConverter.ToUInt32(buffer, offset + attrOffset + 16);
                    fileSize = dataLen;
                    allocatedSize = NativeFileSystem.AlignToCluster(fileSize, clusterSize);
                }
                else if (attrOffset + 56 <= recordSize)
                {
                    allocatedSize = BitConverter.ToInt64(buffer, offset + attrOffset + 40);
                    fileSize = BitConverter.ToInt64(buffer, offset + attrOffset + 48);
                }
            }

            attrOffset += (ushort)attrLen;
        }

        if (string.IsNullOrEmpty(fileName)) return;

        // Gizli MFT metadata dosyalarını filtrele ($MFT, $LogFile vb.)
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
            LastModified = lastWriteTime > DateTime.MinValue ? lastWriteTime : DateTime.Now
        };

        nodes[recordIndex] = node;

        if (isDir)
        {
            Interlocked.Increment(ref _foldersScanned);
        }
        else
        {
            Interlocked.Increment(ref _filesScanned);
            Interlocked.Add(ref _bytesScanned, fileSize);

            lock (topFilesLock)
            {
                if (topFilesQueue.Count < 100)
                {
                    topFilesQueue.Enqueue(new TopFileItem
                    {
                        Name = fileName,
                        DirectoryPath = string.Empty, // Sonradan aşılanacak
                        FullPath = fileName,
                        Extension = Path.GetExtension(fileName),
                        Size = fileSize,
                        AllocatedSize = allocatedSize,
                        LastModified = node.LastModified
                    }, fileSize);
                }
                else if (fileSize > topFilesQueue.Peek().Size)
                {
                    topFilesQueue.Dequeue();
                    topFilesQueue.Enqueue(new TopFileItem
                    {
                        Name = fileName,
                        DirectoryPath = string.Empty,
                        FullPath = fileName,
                        Extension = Path.GetExtension(fileName),
                        Size = fileSize,
                        AllocatedSize = allocatedSize,
                        LastModified = node.LastModified
                    }, fileSize);
                }
            }
        }
    }

    private static void RollupSizes(MftNode node)
    {
        if (node.Children == null || node.Children.Count == 0) return;

        long totalSize = 0;
        long totalAllocated = 0;
        long totalFiles = 0;
        long totalFolders = 0;

        foreach (var child in node.Children)
        {
            if (child.IsDirectory)
            {
                RollupSizes(child);
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

    private static void PopulateFsItemTree(MftNode mftNode, FsItem fsItem, string currentPath)
    {
        if (mftNode.Children == null || mftNode.Children.Count == 0) return;

        var subFolders = new List<FsItem>();
        var directFiles = new List<FsItem>();

        foreach (var child in mftNode.Children)
        {
            string childPath = Path.Combine(currentPath, child.Name);
            var item = new FsItem
            {
                Name = child.Name,
                FullPath = childPath,
                IsDirectory = child.IsDirectory,
                Size = child.Size,
                AllocatedSize = child.AllocatedSize,
                FileCount = child.FileCount,
                FolderCount = child.FolderCount,
                Attributes = child.Attributes,
                LastModified = child.LastModified,
                Parent = fsItem
            };

            if (child.IsDirectory)
            {
                subFolders.Add(item);
                PopulateFsItemTree(child, item, childPath);
            }
            else
            {
                directFiles.Add(item);
            }
        }

        foreach (var sub in subFolders) fsItem.Children.Add(sub);
        foreach (var f in directFiles) fsItem.Children.Add(f);
    }

    private static void CalculatePercentagesAndSort(FsItem item, long rootTotalSize)
    {
        item.PercentOfTotal = rootTotalSize > 0 ? (double)item.Size / rootTotalSize * 100.0 : 0.0;
        if (item.Parent != null && item.Parent.Size > 0)
        {
            item.PercentOfParent = (double)item.Size / item.Parent.Size * 100.0;
        }
        else
        {
            item.PercentOfParent = 100.0;
        }

        if (item.Children.Count > 0)
        {
            TreeMaintenance.SortChildren(item);

            foreach (var child in item.Children)
            {
                CalculatePercentagesAndSort(child, rootTotalSize);
            }
        }
    }

    private void ReportProgressThrottled(string currentPath)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastProgressReport).TotalMilliseconds >= 80)
        {
            _lastProgressReport = now;
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
