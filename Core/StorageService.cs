using System.Management;

namespace PartitionDesk.Core;

public sealed class StorageException : Exception
{
    public StorageException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Disk and partition operations. Everyday operations go through the Windows Storage
/// Management API (the same one Disk Management and PowerShell use); moving, cloning and
/// forced deletes work on the partition table and sectors directly.
/// </summary>
public sealed class StorageService
{
    const string Ns = @"root\Microsoft\Windows\Storage";
    readonly ManagementScope _scope = new(Ns);

    static readonly string[] BusTypes =
    {
        "Unknown", "SCSI", "ATAPI", "ATA", "1394", "SSA", "Fibre Channel", "USB", "RAID", "iSCSI",
        "SAS", "SATA", "SD", "MMC", "Virtual", "Virtual disk file", "Storage Spaces", "NVMe",
    };

    // ---------------------------------------------------------------- reading

    public List<DiskInfo> GetDisks()
    {
        var volumes = new Dictionary<string, ManagementBaseObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in Query("SELECT * FROM MSFT_Volume"))
            if (v["Path"] is string path) volumes[path] = v;

        var partitions = Query("SELECT * FROM MSFT_Partition").Select(ToPartition).ToList();
        foreach (var p in partitions)
        {
            var vpath = p.AccessPaths.FirstOrDefault(a => a.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase));
            if (vpath == null || !volumes.TryGetValue(vpath, out var v)) continue;
            p.VolumePath = vpath;
            p.FileSystem = v["FileSystem"] as string ?? "";
            p.Label = v["FileSystemLabel"] as string ?? "";
            if (p.FileSystem.Length > 0 && v["SizeRemaining"] != null)
            {
                p.FreeSpace = Convert.ToUInt64(v["SizeRemaining"]);
                p.HasVolumeSize = true;
            }
        }

        var disks = new List<DiskInfo>();
        foreach (var d in Query("SELECT * FROM MSFT_Disk"))
        {
            int bus = Convert.ToInt32(d["BusType"] ?? 0);
            var disk = new DiskInfo
            {
                Number = Convert.ToInt32(d["Number"]),
                FriendlyName = (d["FriendlyName"] as string ?? "").Trim(),
                BusType = bus < BusTypes.Length ? BusTypes[bus] : $"Bus {bus}",
                Location = d["Location"] as string ?? "",
                Size = Convert.ToUInt64(d["Size"] ?? 0UL),
                LogicalSectorSize = Convert.ToUInt32(d["LogicalSectorSize"] ?? 512u),
                PhysicalSectorSize = Convert.ToUInt32(d["PhysicalSectorSize"] ?? 512u),
                Style = (PartitionStyle)Convert.ToInt32(d["PartitionStyle"] ?? 0),
                IsOffline = d["IsOffline"] is true,
                IsReadOnly = d["IsReadOnly"] is true,
                IsBoot = d["IsBoot"] is true,
                IsSystem = d["IsSystem"] is true,
            };
            disk.Partitions.AddRange(partitions.Where(p => p.DiskNumber == disk.Number).OrderBy(p => p.Offset));
            ComputeUsableRange(disk);
            BuildSegments(disk);
            disks.Add(disk);
        }
        return disks.OrderBy(d => d.Number).ToList();
    }

    static PartitionInfo ToPartition(ManagementBaseObject o) => new()
    {
        DiskNumber = Convert.ToInt32(o["DiskNumber"]),
        PartitionNumber = Convert.ToInt32(o["PartitionNumber"]),
        Offset = Convert.ToUInt64(o["Offset"]),
        Size = Convert.ToUInt64(o["Size"]),
        DriveLetter = ToChar(o["DriveLetter"]),
        AccessPaths = o["AccessPaths"] as string[] ?? Array.Empty<string>(),
        MbrType = Convert.ToUInt16(o["MbrType"] ?? (ushort)0),
        GptType = o["GptType"] as string ?? "",
        Guid = o["Guid"] as string ?? "",
        IsActive = o["IsActive"] is true,
        IsHidden = o["IsHidden"] is true,
        IsReadOnly = o["IsReadOnly"] is true,
        IsOffline = o["IsOffline"] is true,
        IsSystem = o["IsSystem"] is true,
        IsBoot = o["IsBoot"] is true,
        NoDefaultDriveLetter = o["NoDefaultDriveLetter"] is true,
        WmiPath = (o as ManagementObject)?.Path.Path ?? "",
    };

    static char ToChar(object? v) => v switch
    {
        null => '\0',
        char c => c,
        _ => (char)Convert.ToUInt16(v),
    };

    static void ComputeUsableRange(DiskInfo disk)
    {
        ulong sector = Math.Max(512u, disk.LogicalSectorSize);
        disk.UsableStart = Format.MiB;
        disk.UsableEnd = disk.Style == PartitionStyle.Gpt ? disk.Size - 34 * sector : disk.Size;
        if (disk.Style != PartitionStyle.Gpt) return;
        try
        {
            using var h = Native.Open(Native.PhysicalDrive(disk.Number), write: false);
            var layout = DriveLayout.Read(h);
            if (layout.IsGpt && layout.GptUsableLength > 0)
            {
                disk.UsableStart = (ulong)layout.GptUsableStart;
                disk.UsableEnd = (ulong)(layout.GptUsableStart + layout.GptUsableLength);
            }
        }
        catch { /* keep the estimate */ }
    }

    static void BuildSegments(DiskInfo disk)
    {
        if (disk.Style == PartitionStyle.Raw) return;
        var extended = disk.Partitions.FirstOrDefault(p => p.IsExtended);
        foreach (var p in disk.Partitions)
            if (extended != null && p != extended && p.Offset >= extended.Offset && p.End <= extended.End)
                p.IsLogical = true;

        ulong cursor = disk.UsableStart;
        foreach (var p in disk.Partitions.Where(p => !p.IsExtended))
        {
            AddGap(disk, cursor, p.Offset);
            disk.Segments.Add(new Segment { Disk = disk, Partition = p, Offset = p.Offset, Size = p.Size });
            cursor = Math.Max(cursor, p.End);
        }
        AddGap(disk, cursor, disk.UsableEnd);
    }

    static void AddGap(DiskInfo disk, ulong from, ulong to)
    {
        ulong start = Format.AlignUp(from, Format.MiB);
        ulong end = Format.AlignDown(to, Format.MiB);
        if (end > start && end - start >= Format.MiB)
            disk.Segments.Add(new Segment { Disk = disk, Offset = start, Size = end - start });
    }

    public DiskInfo GetDisk(int number) =>
        GetDisks().FirstOrDefault(d => d.Number == number) ?? throw new StorageException($"Disk {number} no longer exists.");

    // ---------------------------------------------------------------- partitions

    public PartitionInfo CreatePartition(DiskInfo disk, ulong offset, ulong size, PartitionInfo? typeFrom = null)
    {
        if (disk.Style == PartitionStyle.Raw) throw new StorageException("Initialize the disk first.");
        var args = new Dictionary<string, object?>
        {
            ["Offset"] = offset,
            ["Size"] = size,
            ["AssignDriveLetter"] = false,
        };
        // Windows rejects an exact size that runs to the very end of an MBR disk; when the
        // partition should fill its free space, let Windows pick the maximum size instead.
        var gap = disk.Segments.FirstOrDefault(s => s.IsFree && offset >= s.Offset && offset < s.End);
        if (gap != null && offset + size >= gap.End)
        {
            args.Remove("Size");
            args["UseMaximumSize"] = true;
        }
        if (typeFrom != null)
        {
            if (disk.Style == PartitionStyle.Gpt)
                args["GptType"] = typeFrom.GptType.Length > 0 ? typeFrom.GptType : GptTypeForMbr(typeFrom.MbrType);
            else
                args["MbrType"] = typeFrom.MbrType != 0 ? typeFrom.MbrType : MbrTypeFor(typeFrom);
        }
        Invoke(DiskObject(disk.Number), "CreatePartition", args);
        return WaitForPartition(disk.Number, offset);
    }

    static string GptTypeForMbr(ushort mbr) => mbr switch
    {
        0xEF => PartitionTypes.Efi,
        0x27 => PartitionTypes.Recovery,
        _ => PartitionTypes.BasicData,
    };

    static ushort MbrTypeFor(PartitionInfo p)
    {
        if (string.Equals(p.GptType, PartitionTypes.Efi, StringComparison.OrdinalIgnoreCase)) return 0xEF;
        if (string.Equals(p.GptType, PartitionTypes.Recovery, StringComparison.OrdinalIgnoreCase)) return 0x27;
        return p.FileSystem.StartsWith("FAT32", StringComparison.OrdinalIgnoreCase) ? (ushort)0x0C : (ushort)0x07;
    }

    PartitionInfo WaitForPartition(int disk, ulong offset)
    {
        for (int i = 0; i < 40; i++)
        {
            var p = GetDisk(disk).Partitions.FirstOrDefault(x => x.Offset == offset);
            if (p != null && (p.VolumePath.Length > 0 || i > 10)) return p;
            Thread.Sleep(250);
        }
        throw new StorageException("The new partition did not appear.");
    }

    public void FormatVolume(PartitionInfo p, string fileSystem, string label, uint clusterSize, bool full)
    {
        GuardInUse(p, "format");
        var volume = WaitForVolume(p) ?? throw new StorageException("This partition has no volume that can be formatted.");
        var args = new Dictionary<string, object?>
        {
            ["FileSystem"] = fileSystem,
            ["FileSystemLabel"] = label,
            ["Full"] = full,
            ["Force"] = true,
        };
        if (clusterSize > 0) args["AllocationUnitSize"] = clusterSize;
        Invoke(volume, "Format", args);
    }

    ManagementObject? WaitForVolume(PartitionInfo p)
    {
        for (int i = 0; i < 20; i++)
        {
            var part = PartitionObject(p);
            var vol = part.GetRelated("MSFT_Volume").Cast<ManagementObject>().FirstOrDefault();
            if (vol != null) return vol;
            Thread.Sleep(250);
        }
        return null;
    }

    public void SetLabel(PartitionInfo p, string label)
    {
        var volume = WaitForVolume(p) ?? throw new StorageException("This partition has no volume.");
        Invoke(volume, "SetFileSystemLabel", new() { ["FileSystemLabel"] = label });
    }

    public void Delete(PartitionInfo p)
    {
        GuardInUse(p, "delete");
        RemoveMountPoints(p);
        Invoke(PartitionObject(p), "DeleteObject", new());
    }

    /// <summary>
    /// Deletes any partition, including EFI, recovery, OEM and MSR partitions that Windows
    /// normally protects, by clearing the disk's read-only flag, force-dismounting the volume
    /// and removing the entry from the partition table.
    /// </summary>
    public void ForceDelete(PartitionInfo p, IProgress<string>? log = null)
    {
        GuardInUse(p, "delete");
        if (p.IsExtended && GetDisk(p.DiskNumber).Partitions.Any(x => x.IsLogical))
            throw new StorageException("Delete the logical drives inside this extended partition first.");

        var disk = GetDisk(p.DiskNumber);
        if (disk.IsReadOnly)
        {
            log?.Report("Clearing the disk's read-only flag");
            Invoke(DiskObject(disk.Number), "SetAttributes", new() { ["IsReadOnly"] = false });
        }
        if (disk.IsOffline)
        {
            log?.Report("Bringing the disk online");
            Invoke(DiskObject(disk.Number), "Online", new());
        }

        log?.Report("Removing drive letters and mount points");
        RemoveMountPoints(p);

        VolumeLock? volLock = null;
        if (p.VolumePath.Length > 0)
        {
            log?.Report("Dismounting the volume");
            try { volLock = new VolumeLock(p.VolumePath, dismountOnRelease: false); }
            catch (Exception ex) { log?.Report($"Could not lock the volume ({ex.Message}); continuing"); }
        }

        try
        {
            log?.Report("Removing the partition table entry");
            using var h = Native.Open(Native.PhysicalDrive(p.DiskNumber), write: true);
            var layout = DriveLayout.Read(h);
            var entry = layout.FindByOffset(p.Offset)
                ?? throw new StorageException("The partition was not found in the partition table.");
            if (layout.IsGpt)
            {
                layout.Entries.Remove(entry);
            }
            else
            {
                if (p.IsLogical) throw new StorageException("Force delete of logical MBR drives is not supported; use normal delete.");
                Array.Clear(entry.Raw, 8, entry.Raw.Length - 8);
            }
            layout.Write(h);
        }
        finally { volLock?.Dispose(); }
        RefreshCache();
    }

    static void RemoveMountPoints(PartitionInfo p)
    {
        foreach (var path in p.AccessPaths)
            if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
                Native.DeleteVolumeMountPoint(path.EndsWith('\\') ? path : path + "\\");
    }

    public (ulong Min, ulong Max) GetSupportedSize(PartitionInfo p)
    {
        var o = Invoke(PartitionObject(p), "GetSupportedSize", new());
        return (Convert.ToUInt64(o["SizeMin"]), Convert.ToUInt64(o["SizeMax"]));
    }

    public void Resize(PartitionInfo p, ulong newSize)
    {
        Invoke(PartitionObject(p), "Resize", new() { ["Size"] = newSize });
    }

    public void SetDriveLetter(PartitionInfo p, char? letter)
    {
        var part = PartitionObject(p);
        if (p.DriveLetter != '\0')
            Invoke(part, "RemoveAccessPath", new() { ["AccessPath"] = $"{p.DriveLetter}:\\" });
        if (letter is char l)
            Invoke(PartitionObject(p), "AddAccessPath", new() { ["AccessPath"] = $"{l}:\\" });
    }

    public void AutoAssignDriveLetter(PartitionInfo p) =>
        Invoke(PartitionObject(p), "AddAccessPath", new() { ["AssignDriveLetter"] = true });

    public void SetAttributes(PartitionInfo p, bool? active = null, bool? hidden = null, bool? readOnly = null,
        bool? noDefaultLetter = null, string? gptType = null, ushort? mbrType = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["IsActive"] = active,
            ["IsHidden"] = hidden,
            ["IsReadOnly"] = readOnly,
            ["NoDefaultDriveLetter"] = noDefaultLetter,
            ["GptType"] = gptType,
            ["MbrType"] = mbrType,
        };
        Invoke(PartitionObject(p), "SetAttributes", args);
    }

    /// <summary>Runs a file system check; returns a readable result.</summary>
    public string CheckVolume(PartitionInfo p, bool fix)
    {
        var volume = WaitForVolume(p) ?? throw new StorageException("This partition has no volume.");
        var o = Invoke(volume, "Repair", fix ? new() { ["OfflineScanAndFix"] = true } : new() { ["Scan"] = true });
        return Convert.ToUInt32(o["Output"] ?? 0u) switch
        {
            0 => "No errors were found.",
            1 => "Errors were found. Run \"Check and repair\" to fix them.",
            2 => "The file system needs a full repair (offline scan and fix).",
            3 => "Errors were found and fixed.",
            _ => "The check finished.",
        };
    }

    // ---------------------------------------------------------------- move and clone

    /// <summary>Moves a partition to a new start offset on the same disk, copying its sectors.</summary>
    public void Move(PartitionInfo p, ulong newOffset, IProgress<CopyProgress>? progress, IProgress<string>? log)
    {
        GuardInUse(p, "move");
        var disk = GetDisk(p.DiskNumber);
        if (p.IsExtended || p.IsLogical) throw new StorageException("Extended partitions and logical drives cannot be moved.");
        if (newOffset == p.Offset) return;
        if (newOffset % Format.MiB != 0) throw new StorageException("The new position must be aligned to 1 MB.");
        GuardRangeFree(disk, newOffset, p.Size, ignore: p);

        VolumeLock? volLock = null;
        if (p.VolumePath.Length > 0)
        {
            log?.Report("Locking the volume");
            volLock = new VolumeLock(p.VolumePath, dismountOnRelease: true);
        }
        try
        {
            log?.Report("Copying data to the new position");
            RawCopy.Copy(p.DiskNumber, p.Offset, p.DiskNumber, newOffset, p.Size, progress, CancellationToken.None);
            log?.Report("Updating boot sector");
            BootSectorFixer.Fix(p.DiskNumber, newOffset, p.Size, disk.LogicalSectorSize);

            log?.Report("Updating the partition table");
            using var h = Native.Open(Native.PhysicalDrive(p.DiskNumber), write: true);
            var layout = DriveLayout.Read(h);
            var entry = layout.FindByOffset(p.Offset)
                ?? throw new StorageException("The partition was not found in the partition table.");
            entry.StartingOffset = (long)newOffset;
            if (layout.IsMbr) entry.MbrHiddenSectors = (uint)(newOffset / disk.LogicalSectorSize);
            layout.Write(h);
        }
        finally { volLock?.Dispose(); }
        RefreshCache();
    }

    /// <summary>
    /// Copies a partition into free space on any disk. The copy gets the source's size first and
    /// is then grown to <paramref name="targetSize"/> (which also extends NTFS/ReFS file systems).
    /// </summary>
    public PartitionInfo Clone(PartitionInfo source, DiskInfo target, ulong targetOffset, ulong targetSize,
        bool assignLetter, IProgress<CopyProgress>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var srcDisk = GetDisk(source.DiskNumber);
        target = GetDisk(target.Number);
        if (source.IsInUseBySystem)
            throw new StorageException("The running Windows partitions cannot be cloned while Windows is using them.");
        if (source.IsExtended) throw new StorageException("Extended partitions cannot be cloned.");
        if (srcDisk.LogicalSectorSize != target.LogicalSectorSize)
            throw new StorageException("Both disks must use the same sector size.");
        if (targetSize < source.Size) throw new StorageException("The copy must be at least as large as the source.");
        GuardRangeFree(target, targetOffset, targetSize, ignore: null);

        VolumeLock? srcLock = null;
        if (source.VolumePath.Length > 0)
        {
            log?.Report("Locking the source volume");
            srcLock = new VolumeLock(source.VolumePath, dismountOnRelease: false);
        }
        PartitionInfo? created = null;
        try
        {
            log?.Report("Creating the target partition");
            created = CreatePartition(target, targetOffset, source.Size, typeFrom: source);
            using (created.VolumePath.Length > 0 ? new VolumeLock(created.VolumePath, dismountOnRelease: true) : null)
            {
                log?.Report("Copying data");
                RawCopy.Copy(source.DiskNumber, source.Offset, target.Number, targetOffset, source.Size, progress, ct);
                log?.Report("Updating boot sector");
                BootSectorFixer.Fix(target.Number, targetOffset, source.Size, target.LogicalSectorSize);
            }
        }
        catch
        {
            if (created != null)
            {
                log?.Report("Removing the incomplete copy");
                try { ForceDelete(GetDisk(target.Number).Partitions.First(x => x.Offset == targetOffset)); } catch { }
            }
            throw;
        }
        finally { srcLock?.Dispose(); }

        RefreshCache();
        var copy = WaitForPartition(target.Number, targetOffset);
        if (targetSize > source.Size)
        {
            log?.Report("Growing the copy");
            Retry(() => Resize(copy, Format.AlignDown(targetSize, Format.MiB)));
        }
        if (assignLetter)
        {
            log?.Report("Assigning a drive letter");
            try { AutoAssignDriveLetter(WaitForPartition(target.Number, targetOffset)); } catch { }
        }
        return WaitForPartition(target.Number, targetOffset);
    }

    static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch when (i < 8) { Thread.Sleep(500); }
        }
    }

    // ---------------------------------------------------------------- disks

    public void InitializeDisk(DiskInfo d, PartitionStyle style)
    {
        GuardDisk(d);
        Invoke(DiskObject(d.Number), "Initialize", new() { ["PartitionStyle"] = (ushort)style });
    }

    public void ConvertDisk(DiskInfo d, PartitionStyle style)
    {
        GuardDisk(d);
        Invoke(DiskObject(d.Number), "ConvertStyle", new() { ["PartitionStyle"] = (ushort)style });
    }

    /// <summary>Removes every partition, including OEM ones, leaving the disk uninitialized.</summary>
    public void CleanDisk(DiskInfo d)
    {
        GuardDisk(d);
        foreach (var p in d.Partitions) RemoveMountPoints(p);
        Invoke(DiskObject(d.Number), "Clear", new() { ["RemoveData"] = true, ["RemoveOEM"] = true });
    }

    public void SetDiskOnline(DiskInfo d, bool online)
    {
        if (!online) GuardDisk(d);
        Invoke(DiskObject(d.Number), online ? "Online" : "Offline", new());
    }

    public void SetDiskReadOnly(DiskInfo d, bool readOnly)
    {
        if (readOnly) GuardDisk(d);
        Invoke(DiskObject(d.Number), "SetAttributes", new() { ["IsReadOnly"] = readOnly });
    }

    public void RefreshCache()
    {
        try
        {
            using var cls = new ManagementClass(_scope, new ManagementPath("MSFT_StorageSetting"), null);
            cls.InvokeMethod("Update", null, null);
        }
        catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- guards

    /// <summary>Whole-disk operations would take the running Windows down with them.</summary>
    static void GuardDisk(DiskInfo d)
    {
        if (d.HoldsWindows)
            throw new StorageException("This disk holds the running Windows installation.");
    }

    static void GuardInUse(PartitionInfo p, string action)
    {
        if (p.IsInUseBySystem)
            throw new StorageException($"Windows is running from this partition, so it cannot {action} it.");
    }

    static void GuardRangeFree(DiskInfo disk, ulong offset, ulong size, PartitionInfo? ignore)
    {
        ulong end = offset + size;
        if (offset < disk.UsableStart || end > disk.UsableEnd)
            throw new StorageException("The new position is outside the usable area of the disk.");
        var clash = disk.Partitions.FirstOrDefault(x => x != ignore && x.Offset != ignore?.Offset && !x.IsExtended
            && offset < x.End && x.Offset < end);
        if (clash != null) throw new StorageException($"That space overlaps {clash.DisplayName}.");
    }

    // ---------------------------------------------------------------- WMI plumbing

    List<ManagementBaseObject> Query(string wql)
    {
        try
        {
            using var s = new ManagementObjectSearcher(_scope, new ObjectQuery(wql));
            return s.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (ManagementException ex)
        {
            throw new StorageException($"Windows storage query failed: {ex.Message}", ex);
        }
    }

    ManagementObject DiskObject(int number) =>
        Query($"SELECT * FROM MSFT_Disk WHERE Number = {number}").OfType<ManagementObject>().FirstOrDefault()
        ?? throw new StorageException($"Disk {number} was not found.");

    ManagementObject PartitionObject(PartitionInfo p) =>
        Query($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {p.DiskNumber} AND Offset = {p.Offset}")
            .OfType<ManagementObject>().FirstOrDefault()
        ?? throw new StorageException("The partition no longer exists. Refresh and try again.");

    static ManagementBaseObject Invoke(ManagementObject obj, string method, Dictionary<string, object?> args)
    {
        ManagementBaseObject result;
        try
        {
            var inParams = obj.GetMethodParameters(method);
            if (inParams != null)
                foreach (var (k, v) in args)
                    if (v != null) inParams[k] = v;
            result = obj.InvokeMethod(method, inParams, null);
        }
        catch (ManagementException ex)
        {
            throw new StorageException($"{method} failed: {ex.Message}", ex);
        }
        uint code = Convert.ToUInt32(result["ReturnValue"] ?? 0u);
        if (code != 0) throw new StorageException(DescribeError(method, code, result));
        return result;
    }

    static string DescribeError(string method, uint code, ManagementBaseObject result)
    {
        string? detail = null;
        try
        {
            switch (result["ExtendedStatus"])
            {
                case ManagementBaseObject ext:
                    detail = ext["Message"] as string ?? ext["ErrorDescription"] as string;
                    break;
                case string mof:
                    // Embedded instances can arrive serialized as MOF text.
                    var m = System.Text.RegularExpressions.Regex.Match(mof, "Message\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (m.Success) detail = m.Groups[1].Value.Replace("\\\"", "\"");
                    break;
            }
        }
        catch { }
        detail ??= code switch
        {
            1 => "Not supported.",
            2 => "Unspecified error.",
            4 => "Failed.",
            5 => "Invalid parameter.",
            40001 => "Access denied.",
            40002 => "There are not enough resources to complete the operation.",
            41000 => "The disk has not been initialized.",
            41001 => "The disk is already initialized.",
            41002 => "The disk is read-only.",
            41003 => "The disk is offline.",
            41006 => "The disk is not empty.",
            42000 => "There is no free space on the disk at that position.",
            42002 => "The size is not supported by this partition or file system.",
            42004 => "The partition is in use (system, boot, page file or crash dump).",
            42007 => "The requested size is too small or too large.",
            42008 => "The offset is not valid.",
            42010 => "The partition type is not supported for this operation.",
            42012 => "The partition is protected; use Force delete.",
            43000 => "The volume is in use.",
            43006 => "The file system does not support this operation.",
            _ => $"Error code {code}.",
        };
        return $"{method}: {detail}";
    }
}
