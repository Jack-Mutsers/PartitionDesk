using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PartitionDesk.Core;

/// <summary>Raw Win32 disk access: partition tables, volume locks and sector copies.</summary>
internal static unsafe partial class Native
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle h, void* buf, uint count, out uint read, NativeOverlapped* ov);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(SafeFileHandle h, void* buf, uint count, out uint written, NativeOverlapped* ov);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle h);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteVolumeMountPoint(string mountPoint);

    public static Win32Exception LastError(string what)
    {
        int err = Marshal.GetLastPInvokeError();
        return new Win32Exception(err, $"{what}: {new Win32Exception(err).Message}");
    }

    public static SafeFileHandle Open(string path, bool write, bool unbuffered = false)
    {
        uint access = GENERIC_READ | (write ? GENERIC_WRITE : 0);
        uint flags = unbuffered ? FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH : 0;
        var h = CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, flags, 0);
        if (h.IsInvalid) throw LastError($"Cannot open {path}");
        return h;
    }

    public static bool Ioctl(SafeFileHandle h, uint code) =>
        DeviceIoControl(h, code, null, 0, null, 0, out _, 0);

    public static string PhysicalDrive(int number) => $@"\\.\PhysicalDrive{number}";
}

/// <summary>One PARTITION_INFORMATION_EX entry, kept as its raw 144 bytes.</summary>
internal sealed class LayoutEntry
{
    public const int Size = 144;
    public readonly byte[] Raw;
    public LayoutEntry(byte[] raw) => Raw = raw;

    public long StartingOffset
    {
        get => BinaryPrimitives.ReadInt64LittleEndian(Raw.AsSpan(8));
        set => BinaryPrimitives.WriteInt64LittleEndian(Raw.AsSpan(8), value);
    }
    public long Length
    {
        get => BinaryPrimitives.ReadInt64LittleEndian(Raw.AsSpan(16));
        set => BinaryPrimitives.WriteInt64LittleEndian(Raw.AsSpan(16), value);
    }
    public uint Number => BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(24));
    public bool Rewrite { set => Raw[28] = (byte)(value ? 1 : 0); }

    // MBR union (offset 32): type, boot flag, recognized, pad, hidden sectors, partition GUID.
    public byte MbrType { get => Raw[32]; set => Raw[32] = value; }
    public bool MbrBoot { set => Raw[33] = (byte)(value ? 1 : 0); }
    public uint MbrHiddenSectors
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(36));
        set => BinaryPrimitives.WriteUInt32LittleEndian(Raw.AsSpan(36), value);
    }

    // GPT union (offset 32): type GUID, partition GUID, attributes, 36-char name.
    public Guid GptType => new(Raw.AsSpan(32, 16));
    public ulong GptAttributes
    {
        get => BinaryPrimitives.ReadUInt64LittleEndian(Raw.AsSpan(64));
        set => BinaryPrimitives.WriteUInt64LittleEndian(Raw.AsSpan(64), value);
    }
}

/// <summary>DRIVE_LAYOUT_INFORMATION_EX, read and written as raw bytes.</summary>
internal sealed class DriveLayout
{
    const int HeaderSize = 48;
    readonly byte[] _header;
    public readonly List<LayoutEntry> Entries;

    DriveLayout(byte[] header, List<LayoutEntry> entries) { _header = header; Entries = entries; }

    // PARTITION_STYLE in the IOCTL is 0 = MBR, 1 = GPT, 2 = RAW.
    public bool IsMbr => BinaryPrimitives.ReadUInt32LittleEndian(_header) == 0;
    public bool IsGpt => BinaryPrimitives.ReadUInt32LittleEndian(_header) == 1;
    public long GptUsableStart => BinaryPrimitives.ReadInt64LittleEndian(_header.AsSpan(8 + 16));
    public long GptUsableLength => BinaryPrimitives.ReadInt64LittleEndian(_header.AsSpan(8 + 24));

    public static unsafe DriveLayout Read(SafeFileHandle disk)
    {
        int size = HeaderSize + LayoutEntry.Size * 128;
        while (true)
        {
            var buf = new byte[size];
            fixed (byte* p = buf)
            {
                if (Native.DeviceIoControl(disk, Native.IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, 0, p, (uint)size, out _, 0))
                {
                    int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
                    var entries = new List<LayoutEntry>(count);
                    for (int i = 0; i < count; i++)
                        entries.Add(new LayoutEntry(buf.AsSpan(HeaderSize + i * LayoutEntry.Size, LayoutEntry.Size).ToArray()));
                    return new DriveLayout(buf.AsSpan(0, HeaderSize).ToArray(), entries);
                }
                int err = Marshal.GetLastPInvokeError();
                if (err != 122 && err != 234 || size > 1 << 22) throw Native.LastError("Reading the partition table failed");
            }
            size *= 2;
        }
    }

    public unsafe void Write(SafeFileHandle disk)
    {
        foreach (var e in Entries) e.Rewrite = true;
        var buf = new byte[HeaderSize + LayoutEntry.Size * Math.Max(1, Entries.Count)];
        _header.CopyTo(buf, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)Entries.Count);
        for (int i = 0; i < Entries.Count; i++) Entries[i].Raw.CopyTo(buf, HeaderSize + i * LayoutEntry.Size);
        fixed (byte* p = buf)
        {
            if (!Native.DeviceIoControl(disk, Native.IOCTL_DISK_SET_DRIVE_LAYOUT_EX, p, (uint)buf.Length, null, 0, out _, 0))
                throw Native.LastError("Writing the partition table failed");
        }
        Native.Ioctl(disk, Native.IOCTL_DISK_UPDATE_PROPERTIES);
    }

    public LayoutEntry? FindByOffset(ulong offset) =>
        Entries.FirstOrDefault(e => e.Length > 0 && (ulong)e.StartingOffset == offset);
}

/// <summary>Locks and dismounts a volume so its sectors can be rewritten.</summary>
internal sealed class VolumeLock : IDisposable
{
    readonly SafeFileHandle _h;
    readonly bool _dismountOnRelease;

    public VolumeLock(string volumePath, bool dismountOnRelease)
    {
        _dismountOnRelease = dismountOnRelease;
        _h = Native.Open(volumePath.TrimEnd('\\'), write: true);
        if (!Native.Ioctl(_h, Native.FSCTL_LOCK_VOLUME))
        {
            // Files are open on the volume: force a dismount (closes those handles), then lock.
            Native.Ioctl(_h, Native.FSCTL_DISMOUNT_VOLUME);
            if (!Native.Ioctl(_h, Native.FSCTL_LOCK_VOLUME))
            {
                var ex = Native.LastError("The volume is in use and could not be locked");
                _h.Dispose();
                throw ex;
            }
        }
        Native.Ioctl(_h, Native.FSCTL_DISMOUNT_VOLUME);
    }

    public void Dispose()
    {
        if (_h.IsClosed) return;
        // Dismounting makes Windows detect the (new) file system on next access.
        if (_dismountOnRelease) Native.Ioctl(_h, Native.FSCTL_DISMOUNT_VOLUME);
        Native.Ioctl(_h, Native.FSCTL_UNLOCK_VOLUME);
        _h.Dispose();
    }
}

public readonly record struct CopyProgress(ulong Done, ulong Total);

/// <summary>Sector-level copies between byte ranges of physical disks.</summary>
internal static unsafe class RawCopy
{
    const int ChunkSize = 8 * 1024 * 1024;

    public static void Copy(int srcDisk, ulong srcOffset, int dstDisk, ulong dstOffset, ulong length,
        IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        using var src = Native.Open(Native.PhysicalDrive(srcDisk), write: srcDisk == dstDisk, unbuffered: true);
        using var dstOwned = srcDisk == dstDisk ? null : Native.Open(Native.PhysicalDrive(dstDisk), write: true, unbuffered: true);
        var dst = dstOwned ?? src;

        // When the target overlaps the source further along, copy back to front.
        bool backwards = srcDisk == dstDisk && dstOffset > srcOffset && dstOffset < srcOffset + length;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(ChunkSize, 4096);
        try
        {
            ulong done = 0;
            while (done < length)
            {
                ct.ThrowIfCancellationRequested();
                uint n = (uint)Math.Min((ulong)ChunkSize, length - done);
                ulong rel = backwards ? length - done - n : done;
                ReadAt(src, srcOffset + rel, buf, n);
                WriteAt(dst, dstOffset + rel, buf, n);
                done += n;
                progress?.Report(new CopyProgress(done, length));
            }
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    public static void ReadAt(SafeFileHandle h, ulong offset, byte* buf, uint n)
    {
        var ov = new NativeOverlapped { OffsetLow = (int)(uint)offset, OffsetHigh = (int)(uint)(offset >> 32) };
        if (!Native.ReadFile(h, buf, n, out uint read, &ov) || read != n)
            throw Native.LastError($"Reading the disk at {Format.Bytes(offset)} failed");
    }

    public static void WriteAt(SafeFileHandle h, ulong offset, byte* buf, uint n)
    {
        var ov = new NativeOverlapped { OffsetLow = (int)(uint)offset, OffsetHigh = (int)(uint)(offset >> 32) };
        if (!Native.WriteFile(h, buf, n, out uint written, &ov) || written != n)
            throw Native.LastError($"Writing the disk at {Format.Bytes(offset)} failed");
    }
}

/// <summary>
/// Updates the "partition start" fields file systems keep in their boot sectors,
/// so a moved or cloned volume stays bootable and consistent.
/// </summary>
internal static unsafe class BootSectorFixer
{
    public static void Fix(int disk, ulong partOffset, ulong partSize, uint sectorSize)
    {
        using var h = Native.Open(Native.PhysicalDrive(disk), write: true, unbuffered: true);
        uint regionLen = sectorSize * 24;
        byte* buf = (byte*)NativeMemory.AlignedAlloc(regionLen, 4096);
        try
        {
            RawCopy.ReadAt(h, partOffset, buf, regionLen);
            var region = new Span<byte>(buf, (int)regionLen);
            var oem = Encoding.ASCII.GetString(region.Slice(3, 8));
            ulong startLba = partOffset / sectorSize;

            if (oem == "NTFS    ")
            {
                SetHidden(h, partOffset, buf, sectorSize, startLba);
                // NTFS keeps a backup boot sector right after the last volume sector.
                ulong total = BinaryPrimitives.ReadUInt64LittleEndian(region.Slice(0x28));
                ulong backup = partOffset + total * sectorSize;
                if (backup + sectorSize <= partOffset + partSize) FixAt(h, backup, sectorSize, startLba, "NTFS    ");
            }
            else if (Encoding.ASCII.GetString(region.Slice(0x52, 8)) == "FAT32   ")
            {
                SetHidden(h, partOffset, buf, sectorSize, startLba);
                ushort backupSector = BinaryPrimitives.ReadUInt16LittleEndian(region.Slice(0x32));
                if (backupSector is > 0 and < 32) FixAt(h, partOffset + backupSector * sectorSize, sectorSize, startLba, null);
            }
            else if (Encoding.ASCII.GetString(region.Slice(0x36, 8)).StartsWith("FAT1"))
            {
                SetHidden(h, partOffset, buf, sectorSize, startLba);
            }
            else if (oem == "EXFAT   ")
            {
                FixExFat(h, partOffset, buf, sectorSize, startLba);
                FixExFat(h, partOffset + 12UL * sectorSize, buf + 12 * sectorSize, sectorSize, startLba);
            }
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    static void SetHidden(SafeFileHandle h, ulong at, byte* sector, uint sectorSize, ulong startLba)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(sector + 0x1C, 4), (uint)Math.Min(startLba, uint.MaxValue));
        RawCopy.WriteAt(h, at, sector, sectorSize);
    }

    static void FixAt(SafeFileHandle h, ulong at, uint sectorSize, ulong startLba, string? oem)
    {
        byte* s = (byte*)NativeMemory.AlignedAlloc(sectorSize, 4096);
        try
        {
            RawCopy.ReadAt(h, at, s, sectorSize);
            if (oem != null && Encoding.ASCII.GetString(new ReadOnlySpan<byte>(s + 3, 8)) != oem) return;
            SetHidden(h, at, s, sectorSize, startLba);
        }
        finally { NativeMemory.AlignedFree(s); }
    }

    /// <summary>exFAT stores the partition offset at 0x40 and checksums the 11-sector boot region.</summary>
    static void FixExFat(SafeFileHandle h, ulong at, byte* region, uint sectorSize, ulong startLba)
    {
        if (Encoding.ASCII.GetString(new ReadOnlySpan<byte>(region + 3, 8)) != "EXFAT   ") return;
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(region + 0x40, 8), startLba);
        uint sum = 0;
        int len = (int)sectorSize * 11;
        for (int i = 0; i < len; i++)
        {
            if (i is 106 or 107 or 112) continue;
            sum = ((sum & 1) != 0 ? 0x80000000u : 0) + (sum >> 1) + region[i];
        }
        var check = new Span<byte>(region + len, (int)sectorSize);
        for (int i = 0; i < check.Length; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(check.Slice(i), sum);
        RawCopy.WriteAt(h, at, region, sectorSize * 12);
    }
}
