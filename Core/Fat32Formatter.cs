using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PartitionDesk.Core;

/// <summary>
/// Writes a FAT32 file system directly, like fat32format/guiformat. Windows' own format refuses
/// FAT32 above 32 GB, but the file system itself works up to 2 TB (with 512-byte sectors), and
/// devices such as game consoles and cameras only accept FAT32.
/// </summary>
internal static unsafe class Fat32Formatter
{
    const uint ReservedSectors = 32;
    const uint NumFats = 2;
    const uint MinClusters = 65525;
    const uint MaxClusters = 0x0FFFFFF5;

    /// <summary>Cluster size Windows itself would pick, carried on above 32 GB.</summary>
    public static uint DefaultClusterSize(ulong size) => size switch
    {
        <= 8 * Format.GiB => 4096,
        <= 16 * Format.GiB => 8192,
        <= 32 * Format.GiB => 16384,
        _ => 32768,
    };

    /// <summary>Returns why the volume cannot be FAT32 with this cluster size, or null if it can.</summary>
    public static string? Check(ulong size, uint sectorSize, uint clusterSize)
    {
        if (clusterSize == 0) clusterSize = DefaultClusterSize(size);
        if (clusterSize < sectorSize || clusterSize % sectorSize != 0 || clusterSize > 65536)
            return $"An allocation unit of {clusterSize / 1024} KB is not possible on this disk.";
        if (size / sectorSize > uint.MaxValue)
            return $"FAT32 volumes can be at most {Format.Bytes(uint.MaxValue * (ulong)sectorSize)} on this disk.";
        var layout = Plan(size, sectorSize, clusterSize);
        if (layout.Clusters < MinClusters) return "The volume is too small for FAT32 with this allocation unit size.";
        if (layout.Clusters >= MaxClusters) return "Too many clusters for FAT32: choose a larger allocation unit size.";
        return null;
    }

    readonly record struct Layout(uint TotalSectors, uint SectorsPerCluster, uint Reserved, uint FatSectors, uint Clusters);

    static Layout Plan(ulong size, uint sectorSize, uint clusterSize)
    {
        uint total = (uint)Math.Min(size / sectorSize, uint.MaxValue);
        uint spc = clusterSize / sectorSize;
        // FAT size as computed by fat32format: an upper bound that covers every cluster.
        ulong fat = 4UL * (total - ReservedSectors) / ((ulong)spc * sectorSize + 4 * NumFats) + 1;
        // Pad the reserved area so the data region starts on a cluster (and 1 MB, when possible) boundary.
        uint align = (uint)Math.Max(spc, Format.MiB / sectorSize);
        uint reserved = ReservedSectors;
        uint dataStart = reserved + NumFats * (uint)fat;
        if (dataStart % align != 0) reserved += align - dataStart % align;
        uint clusters = (uint)((total - reserved - NumFats * fat) / spc);
        // The FAT holds an entry for every cluster plus the two reserved entries.
        clusters = (uint)Math.Min(clusters, fat * sectorSize / 4 - 2);
        return new Layout(total, spc, reserved, (uint)fat, clusters);
    }

    /// <summary>
    /// Formats the byte range of a partition. The caller must have locked its volume (or it has none).
    /// </summary>
    public static void Write(int disk, ulong offset, ulong size, uint sectorSize, uint clusterSize, string label)
    {
        if (clusterSize == 0) clusterSize = DefaultClusterSize(size);
        var problem = Check(size, sectorSize, clusterSize);
        if (problem != null) throw new StorageException(problem);
        var l = Plan(size, sectorSize, clusterSize);
        var volumeLabel = LabelBytes(label);

        using var h = Native.Open(Native.PhysicalDrive(disk), write: true, unbuffered: true);
        const uint chunk = 4 * 1024 * 1024;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(chunk, 4096);
        try
        {
            // Zero the reserved area, both FATs and the root directory cluster.
            ulong clearEnd = ((ulong)l.Reserved + NumFats * l.FatSectors + l.SectorsPerCluster) * sectorSize;
            NativeMemory.Clear(buf, chunk);
            for (ulong pos = 0; pos < clearEnd; pos += chunk)
                RawCopy.WriteAt(h, offset + pos, buf, (uint)Math.Min(chunk, clearEnd - pos));

            // Boot sector, FSInfo and their backups at sectors 6 and 7.
            var boot = new Span<byte>(buf, (int)sectorSize);
            boot.Clear();
            WriteBootSector(boot, l, sectorSize, offset / sectorSize, volumeLabel);
            RawCopy.WriteAt(h, offset, buf, sectorSize);
            RawCopy.WriteAt(h, offset + 6UL * sectorSize, buf, sectorSize);

            boot.Clear();
            WriteFsInfo(boot, l.Clusters - 1);
            RawCopy.WriteAt(h, offset + 1UL * sectorSize, buf, sectorSize);
            RawCopy.WriteAt(h, offset + 7UL * sectorSize, buf, sectorSize);

            // First FAT sector: media descriptor, end-of-chain marker, and cluster 2 = root directory.
            boot.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(boot, 0x0FFFFFF8);
            BinaryPrimitives.WriteUInt32LittleEndian(boot[4..], 0x0FFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(boot[8..], 0x0FFFFFFF);
            for (uint i = 0; i < NumFats; i++)
                RawCopy.WriteAt(h, offset + ((ulong)l.Reserved + i * l.FatSectors) * sectorSize, buf, sectorSize);

            // Volume label entry in the root directory.
            if (label.Length > 0)
            {
                boot.Clear();
                volumeLabel.CopyTo(boot);
                boot[11] = 0x08;
                RawCopy.WriteAt(h, offset + ((ulong)l.Reserved + NumFats * l.FatSectors) * sectorSize, buf, sectorSize);
            }
            Native.FlushFileBuffers(h);
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    static byte[] LabelBytes(string label)
    {
        var clean = new string(label.ToUpperInvariant().Where(c => c >= ' ' && c < 127 && !"*?.,;:/\\|+=<>[]\"".Contains(c)).Take(11).ToArray());
        return Encoding.ASCII.GetBytes((clean.Length > 0 ? clean : "NO NAME").PadRight(11));
    }

    static void WriteBootSector(Span<byte> s, Layout l, uint sectorSize, ulong hiddenSectors, byte[] label)
    {
        s[0] = 0xEB; s[1] = 0x58; s[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(s[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0B..], (ushort)sectorSize);
        s[0x0D] = (byte)l.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x0E..], (ushort)l.Reserved);
        s[0x10] = (byte)NumFats;
        s[0x15] = 0xF8;                                                       // fixed disk
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x18..], 63);              // sectors per track
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x1A..], 255);             // heads
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1C..], (uint)Math.Min(hiddenSectors, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x20..], l.TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x24..], l.FatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x2C..], 2);               // root directory cluster
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x30..], 1);               // FSInfo sector
        BinaryPrimitives.WriteUInt16LittleEndian(s[0x32..], 6);               // backup boot sector
        s[0x40] = 0x80;                                                       // drive number
        s[0x42] = 0x29;                                                       // extended boot signature
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x43..], (uint)Environment.TickCount64 ^ (uint)DateTime.Now.Ticks);
        label.CopyTo(s[0x47..]);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(s[0x52..]);
        s[510] = 0x55; s[511] = 0xAA;
    }

    static void WriteFsInfo(Span<byte> s, uint freeClusters)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1E4..], 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1E8..], freeClusters);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1EC..], 3);              // next free cluster hint
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1FC..], 0xAA550000);
    }
}
