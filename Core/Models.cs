namespace PartitionDesk.Core;

public enum PartitionStyle { Raw = 0, Mbr = 1, Gpt = 2 }

public sealed class DiskInfo
{
    public int Number { get; init; }
    public string FriendlyName { get; init; } = "";
    public string BusType { get; init; } = "";
    public string Location { get; init; } = "";
    public ulong Size { get; init; }
    public uint LogicalSectorSize { get; init; }
    public uint PhysicalSectorSize { get; init; }
    public PartitionStyle Style { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    /// <summary>Byte range partitions may occupy (excludes MBR/GPT structures).</summary>
    public ulong UsableStart { get; set; }
    public ulong UsableEnd { get; set; }
    public List<PartitionInfo> Partitions { get; } = new();

    /// <summary>The disk map: partitions and unallocated gaps in disk order.</summary>
    public List<Segment> Segments { get; } = new();

    public bool HoldsWindows => IsBoot || IsSystem;
    public string Title => $"Disk {Number}";
    public string Summary => $"{FriendlyName} · {Format.Bytes(Size)} · {StyleText}";
    public string StyleText => Style switch
    {
        PartitionStyle.Gpt => "GPT",
        PartitionStyle.Mbr => "MBR",
        _ => "Not initialized",
    };
    public string StatusText
    {
        get
        {
            var flags = new List<string>();
            if (IsOffline) flags.Add("Offline");
            if (IsReadOnly) flags.Add("Read-only");
            if (HoldsWindows) flags.Add("Windows disk");
            flags.Add(BusType);
            return string.Join(" · ", flags);
        }
    }
}

public sealed class PartitionInfo
{
    public int DiskNumber { get; init; }
    public int PartitionNumber { get; init; }
    public ulong Offset { get; init; }
    public ulong Size { get; init; }
    public char DriveLetter { get; init; }
    public string[] AccessPaths { get; init; } = Array.Empty<string>();
    public ushort MbrType { get; init; }
    public string GptType { get; init; } = "";
    public string Guid { get; init; } = "";
    public bool IsActive { get; init; }
    public bool IsHidden { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsOffline { get; init; }
    public bool IsSystem { get; init; }
    public bool IsBoot { get; init; }
    public bool NoDefaultDriveLetter { get; init; }
    public string WmiPath { get; init; } = "";

    // Filled from the matching volume, if any.
    public string VolumePath { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public string Label { get; set; } = "";
    public ulong FreeSpace { get; set; }
    public bool HasVolumeSize { get; set; }

    public ulong End => Offset + Size;
    public string TypeName => PartitionTypes.Describe(this);
    public bool IsProtectedType => PartitionTypes.IsProtected(this);
    public bool IsExtended => MbrType is 0x05 or 0x0F or 0x85;
    public bool IsLogical { get; set; }
    /// <summary>The running Windows installation needs this partition.</summary>
    public bool IsInUseBySystem => IsSystem || IsBoot || PartitionTypes.IsPageFileVolume(this);
    public string LetterText => DriveLetter != '\0' ? $"{DriveLetter}:" : "";
    public string DisplayName
    {
        get
        {
            var name = LetterText;
            if (Label.Length > 0) name = name.Length > 0 ? $"{name} {Label}" : Label;
            return name.Length > 0 ? name : $"Partition {PartitionNumber}";
        }
    }
    public string FlagsText
    {
        get
        {
            var f = new List<string>();
            if (IsBoot) f.Add("Boot");
            if (IsSystem) f.Add("System");
            if (IsActive) f.Add("Active");
            if (IsHidden) f.Add("Hidden");
            if (IsReadOnly) f.Add("Read-only");
            if (IsOffline) f.Add("Offline");
            if (IsProtectedType) f.Add("Protected");
            return string.Join(", ", f);
        }
    }
}

/// <summary>One block of the disk map: a partition or unallocated space.</summary>
public sealed class Segment
{
    public DiskInfo Disk { get; init; } = null!;
    public PartitionInfo? Partition { get; init; }
    public ulong Offset { get; init; }
    public ulong Size { get; init; }
    public ulong End => Offset + Size;
    public bool IsFree => Partition == null;

    public string Name => Partition?.DisplayName ?? "Unallocated";
    public string Type => Partition?.TypeName ?? "";
    public string FileSystem => Partition?.FileSystem ?? "";
    public string SizeText => Format.Bytes(Size);
    public string UsedText => Partition is { HasVolumeSize: true } p ? Format.Bytes(Size - Math.Min(Size, p.FreeSpace)) : "";
    public string FreeText => Partition is { HasVolumeSize: true } p ? Format.Bytes(p.FreeSpace) : "";
    public string OffsetText => Format.Bytes(Offset);
    public string Flags => Partition?.FlagsText ?? "";
    public double UsedFraction => Partition is { HasVolumeSize: true } p && Size > 0
        ? Math.Clamp(1.0 - (double)p.FreeSpace / Size, 0, 1) : 0;
}

public static class Format
{
    public const ulong MiB = 1024 * 1024;
    public const ulong GiB = MiB * 1024;

    public static string Bytes(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }

    public static ulong AlignUp(ulong v, ulong a) => (v + a - 1) / a * a;
    public static ulong AlignDown(ulong v, ulong a) => v / a * a;
}

public static class PartitionTypes
{
    public const string BasicData = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";
    public const string Efi = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    public const string Msr = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    public const string Recovery = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
    public const string LdmMeta = "{5808c8aa-7e8f-42e0-85d2-e1e90434cfb3}";
    public const string LdmData = "{af9b60a0-1431-4f62-bc68-3311714a69ad}";
    public const string StorageSpaces = "{e75caf8f-f680-4cee-afa3-b001e56efc2d}";

    static readonly Dictionary<string, string> GptNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [BasicData] = "Basic data",
        [Efi] = "EFI System",
        [Msr] = "Microsoft Reserved",
        [Recovery] = "Recovery",
        [LdmMeta] = "LDM metadata",
        [LdmData] = "LDM data",
        [StorageSpaces] = "Storage Spaces",
        ["{0fc63daf-8483-4772-8e79-3d69d8477de4}"] = "Linux filesystem",
        ["{0657fd6d-a4ab-43c4-84e5-0933c84b4f4f}"] = "Linux swap",
        ["{e6d6d379-f507-44c2-a23c-238f2a3df928}"] = "Linux LVM",
        ["{48465300-0000-11aa-aa11-00306543ecac}"] = "Apple HFS+",
        ["{7c3457ef-0000-11aa-aa11-00306543ecac}"] = "Apple APFS",
        ["{21686148-6449-6e6f-744e-656564454649}"] = "BIOS boot",
    };

    public static string Describe(PartitionInfo p)
    {
        if (p.GptType.Length > 0)
            return GptNames.TryGetValue(p.GptType, out var n) ? n : p.GptType;
        return p.MbrType switch
        {
            0x01 => "FAT12",
            0x04 or 0x06 or 0x0E => "FAT16",
            0x07 => "IFS (NTFS/exFAT)",
            0x0B or 0x0C => "FAT32",
            0x05 or 0x0F => "Extended",
            0x12 or 0x27 => "OEM / Recovery",
            0x42 => "LDM",
            0x82 => "Linux swap",
            0x83 => "Linux",
            0x8E => "Linux LVM",
            0xEE => "GPT protective",
            0xEF => "EFI System",
            _ => $"Type 0x{p.MbrType:X2}",
        };
    }

    /// <summary>Partitions Windows refuses to delete without an override.</summary>
    public static bool IsProtected(PartitionInfo p)
    {
        if (p.GptType.Length > 0)
            return !string.Equals(p.GptType, BasicData, StringComparison.OrdinalIgnoreCase);
        return p.MbrType is 0x12 or 0x27 or 0x42 or 0xEF or 0xDE or 0x84;
    }

    public static bool IsPageFileVolume(PartitionInfo p)
    {
        if (p.DriveLetter == '\0') return false;
        var sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        return char.ToUpperInvariant(sysDrive[0]) == char.ToUpperInvariant(p.DriveLetter);
    }
}
