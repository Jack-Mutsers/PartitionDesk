using System.Security.Cryptography;
using PartitionDesk.Core;

// Usage: SelfTest <diskNumber> <vhdPath> [boot]   ("boot" runs only the bootable-media tests)
// Refuses to run unless the disk is the given virtual disk file.
int diskNo = int.Parse(args[0]);
string vhd = args[1];
var s = new StorageService();
int failures = 0;
const ulong MB = Format.MiB;

var disk = s.GetDisk(diskNo);
if (disk.BusType != "Virtual disk file" || !string.Equals(Path.GetFullPath(disk.Location), Path.GetFullPath(vhd), StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"ABORT: disk {diskNo} is '{disk.FriendlyName}' ({disk.BusType}, {disk.Location}), not the test VHD.");
    return 2;
}
Console.WriteLine($"Testing on disk {diskNo}: {disk.Location}, {Format.Bytes(disk.Size)}");

void Step(string name, Action a)
{
    try { a(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}"); }
}

PartitionInfo At(ulong offset) => s.GetDisk(diskNo).Partitions.First(p => p.Offset == offset);

string Root(ulong offset)
{
    for (int i = 0; i < 40; i++)
    {
        var p = At(offset);
        if (p.DriveLetter != '\0' && Directory.Exists($"{p.DriveLetter}:\\")) return $"{p.DriveLetter}:\\";
        if (i == 8 && p.DriveLetter == '\0') s.AutoAssignDriveLetter(p);
        Thread.Sleep(500);
    }
    throw new Exception($"No mounted drive letter for partition at {offset / MB} MB");
}

string WriteSample(ulong offset)
{
    var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
    File.WriteAllBytes(Path.Combine(Root(offset), "sample.bin"), data);
    return Convert.ToHexString(SHA256.HashData(data));
}

void Verify(ulong offset, string hash, string what)
{
    var data = File.ReadAllBytes(Path.Combine(Root(offset), "sample.bin"));
    if (Convert.ToHexString(SHA256.HashData(data)) != hash) throw new Exception($"{what}: file content changed");
}

void Expect<T>(Action a) where T : Exception
{
    try { a(); } catch (T ex) { Console.WriteLine($"      (refused: {ex.Message})"); return; }
    throw new Exception($"expected {typeof(T).Name}");
}

string hashA = "", hashB = "", hashC = "";

if (args.Length < 3 || args[2] != "boot") {
// ------------------------------------------------------------- GPT (Windows puts a 16 MB MSR partition at 1 MB)
Step("Initialize GPT", () => s.InitializeDisk(s.GetDisk(diskNo), PartitionStyle.Gpt));
Step("Create + format NTFS A at 32 MB", () =>
{
    var p = s.CreatePartition(s.GetDisk(diskNo), 32 * MB, 200 * MB);
    s.FormatVolume(p, "NTFS", "A", 0, false);
    s.AutoAssignDriveLetter(At(32 * MB));
    hashA = WriteSample(32 * MB);
});
Step("Create + format FAT32 B at 400 MB", () =>
{
    var p = s.CreatePartition(s.GetDisk(diskNo), 400 * MB, 100 * MB);
    s.FormatVolume(p, "FAT32", "B", 0, false);
    s.AutoAssignDriveLetter(At(400 * MB));
    hashB = WriteSample(400 * MB);
});
Step("Shrink A to 150 MB", () =>
{
    s.Resize(At(32 * MB), 150 * MB);
    if (At(32 * MB).Size != 150 * MB) throw new Exception("size not changed");
    Verify(32 * MB, hashA, "A");
});
Step("Extend A to 180 MB", () =>
{
    s.Resize(At(32 * MB), 180 * MB);
    if (At(32 * MB).Size != 180 * MB) throw new Exception("size not changed");
    Verify(32 * MB, hashA, "A");
});
Step("Move A right (overlapping) to 100 MB", () =>
{
    s.Move(At(32 * MB), 100 * MB, null, null);
    Verify(100 * MB, hashA, "A");
});
Step("Move A left (overlapping) back to 32 MB", () =>
{
    s.Move(At(100 * MB), 32 * MB, null, null);
    Verify(32 * MB, hashA, "A");
});
Step("Move FAT32 B to 600 MB", () =>
{
    s.Move(At(400 * MB), 600 * MB, null, null);
    Verify(600 * MB, hashB, "B");
});
Step("Moving onto another partition is refused", () =>
    Expect<StorageException>(() => s.Move(At(600 * MB), 100 * MB, null, null)));
Step("Clone A to 300 MB, grown to 250 MB", () =>
{
    var copy = s.Clone(At(32 * MB), s.GetDisk(diskNo), 300 * MB, 250 * MB, true, null, null, CancellationToken.None);
    if (copy.Size != 250 * MB) throw new Exception($"clone size {copy.Size / MB} MB");
    Verify(300 * MB, hashA, "clone");
    Verify(32 * MB, hashA, "A after clone");
});
Step("Create + format exFAT C at 720 MB, move to 760 MB", () =>
{
    var p = s.CreatePartition(s.GetDisk(diskNo), 720 * MB, 64 * MB);
    s.FormatVolume(p, "exFAT", "C", 0, false);
    s.AutoAssignDriveLetter(At(720 * MB));
    hashC = WriteSample(720 * MB);
    s.Move(At(720 * MB), 760 * MB, null, null);
    Verify(760 * MB, hashC, "C");
});
Step("Force delete recovery partition", () =>
{
    s.SetAttributes(At(300 * MB), gptType: PartitionTypes.Recovery);
    if (!At(300 * MB).IsProtectedType) throw new Exception("type not changed");
    s.ForceDelete(At(300 * MB));
    if (s.GetDisk(diskNo).Partitions.Any(p => p.Offset == 300 * MB)) throw new Exception("still there");
});
Step("Force delete EFI partition", () =>
{
    s.CreatePartition(s.GetDisk(diskNo), 300 * MB, 100 * MB);
    s.SetAttributes(At(300 * MB), gptType: PartitionTypes.Efi);
    s.ForceDelete(At(300 * MB));
    if (s.GetDisk(diskNo).Partitions.Any(p => p.Offset == 300 * MB)) throw new Exception("still there");
});
Step("Force delete a mounted volume with open file", () =>
{
    using var open = File.OpenRead(Path.Combine(Root(600 * MB), "sample.bin"));
    s.ForceDelete(At(600 * MB));
    if (s.GetDisk(diskNo).Partitions.Any(p => p.Offset == 600 * MB)) throw new Exception("still there");
});
Step("Force delete on a read-only disk", () =>
{
    s.SetDiskReadOnly(s.GetDisk(diskNo), true);
    s.ForceDelete(At(760 * MB));
    if (s.GetDisk(diskNo).IsReadOnly) throw new Exception("disk still read-only");
});
Step("Normal delete A", () => s.Delete(At(32 * MB)));
Step("Clean disk", () =>
{
    s.CleanDisk(s.GetDisk(diskNo));
    if (s.GetDisk(diskNo).Style != PartitionStyle.Raw) throw new Exception("disk still initialized");
});

// ------------------------------------------------------------- MBR
Step("Initialize MBR", () => s.InitializeDisk(s.GetDisk(diskNo), PartitionStyle.Mbr));
Step("MBR: create NTFS, move right, verify", () =>
{
    var p = s.CreatePartition(s.GetDisk(diskNo), 1 * MB, 120 * MB);
    s.FormatVolume(p, "NTFS", "M", 0, false);
    s.AutoAssignDriveLetter(At(1 * MB));
    var h = WriteSample(1 * MB);
    s.Move(At(1 * MB), 50 * MB, null, null);
    Verify(50 * MB, h, "MBR move");
    s.SetAttributes(At(50 * MB), active: true);
    if (!At(50 * MB).IsActive) throw new Exception("not active");
});
Step("MBR: force delete OEM (0x27) partition", () =>
{
    s.CreatePartition(s.GetDisk(diskNo), 300 * MB, 50 * MB);
    s.SetAttributes(At(300 * MB), mbrType: 0x27);
    s.ForceDelete(At(300 * MB));
    if (s.GetDisk(diskNo).Partitions.Any(p => p.Offset == 300 * MB)) throw new Exception("still there");
    if (!s.GetDisk(diskNo).Partitions.Any(p => p.Offset == 50 * MB)) throw new Exception("other partition lost");
});
Step("Clean + convert to GPT", () =>
{
    s.CleanDisk(s.GetDisk(diskNo));
    s.InitializeDisk(s.GetDisk(diskNo), PartitionStyle.Mbr);
    s.ConvertDisk(s.GetDisk(diskNo), PartitionStyle.Gpt);
    if (s.GetDisk(diskNo).Style != PartitionStyle.Gpt) throw new Exception("still MBR");
});

}

// ------------------------------------------------------------- bootable media
var trace = new SyncLog();
var media = new BootableMediaService(s);
string winIso = Path.Combine(AppContext.BaseDirectory, "..", "..", "test-win.iso");
string hybridIso = Path.Combine(AppContext.BaseDirectory, "..", "..", "test-hybrid.iso");
IsoInfo? win = null;

Step("Inspect Windows-style ISO", () =>
{
    win = media.Inspect(winIso);
    Console.WriteLine($"      {win.Describe()}");
    if (!win.IsWindows || !win.HasEfi || win.IsHybrid || win.Recommended != BootMode.CopyFiles) throw new Exception("wrong detection");
});
Step("Inspect hybrid ISO", () =>
{
    var h = media.Inspect(hybridIso);
    if (!h.IsHybrid) throw new Exception("not detected as hybrid");
});

void CheckCopied(PartitionStyle style)
{
    var d = s.GetDisk(diskNo);
    if (d.Style != style) throw new Exception($"disk is {d.Style}");
    var p = d.Partitions.Single(x => x.FileSystem == "FAT32");
    if (style == PartitionStyle.Mbr && !p.IsActive) throw new Exception("partition not active");
    string root = Root(p.Offset);
    using var iso = IsoMount.Mount(winIso);
    foreach (var f in Directory.EnumerateFiles(iso.Root, "*", SearchOption.AllDirectories))
    {
        var copy = Path.Combine(root, Path.GetRelativePath(iso.Root, f));
        if (!File.Exists(copy)) throw new Exception($"missing {copy}");
        if (!SHA256.HashData(File.ReadAllBytes(f)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(copy))))
            throw new Exception($"content differs: {copy}");
    }
}

Step("Bootable: copy files, MBR (disk had partitions)", () =>
{
    if (s.GetDisk(diskNo).Style == PartitionStyle.Raw) s.InitializeDisk(s.GetDisk(diskNo), PartitionStyle.Gpt);
    s.CreatePartition(s.GetDisk(diskNo), 64 * MB, 100 * MB);
    media.Create(win!, s.GetDisk(diskNo), BootMode.CopyFiles, BootScheme.MbrBiosAndUefi, "pd test", false, null, null, CancellationToken.None);
    CheckCopied(PartitionStyle.Mbr);
});
Step("Bootable: copy files, GPT", () =>
{
    media.Create(win!, s.GetDisk(diskNo), BootMode.CopyFiles, BootScheme.GptUefiOnly, "pd test", false, null, null, CancellationToken.None);
    CheckCopied(PartitionStyle.Gpt);
});
unsafe void RawMatches(string isoPath)
{
    var expected = File.ReadAllBytes(isoPath);
    var len = (uint)Format.AlignUp((ulong)expected.Length, 4096);
    byte* buf = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(len, 4096);
    try
    {
        using var h = Native.Open(Native.PhysicalDrive(diskNo), write: false, unbuffered: true);
        RawCopy.ReadAt(h, 0, buf, len);
        if (!new Span<byte>(buf, expected.Length).SequenceEqual(expected)) throw new Exception("disk content differs from image");
    }
    finally { System.Runtime.InteropServices.NativeMemory.AlignedFree(buf); }
}
Step("Bootable: raw image with verify", () =>
{
    var h = media.Inspect(hybridIso);
    media.Create(h, s.GetDisk(diskNo), BootMode.RawImage, BootScheme.MbrBiosAndUefi, "", true, null, null, CancellationToken.None);
    RawMatches(hybridIso);
    var d = s.GetDisk(diskNo);
    if (d.Style != PartitionStyle.Mbr || d.Partitions.Count != 1) throw new Exception($"Windows sees {d.StyleText} with {d.Partitions.Count} partitions");
});
Step("Bootable: copy files over a raw-written disk", () =>
{
    try { media.Create(win!, s.GetDisk(diskNo), BootMode.CopyFiles, BootScheme.MbrBiosAndUefi, "again", false, null, trace, CancellationToken.None); }
    catch
    {
        var d = s.GetDisk(diskNo);
        Console.WriteLine($"      disk now: {d.StyleText}, {d.Partitions.Count} partitions, offline={d.IsOffline}, ro={d.IsReadOnly}");
        foreach (var p in d.Partitions) Console.WriteLine($"        {p.Offset / MB} MB {p.Size / MB} MB {p.TypeName} {p.FileSystem}");
        throw;
    }
    CheckCopied(PartitionStyle.Mbr);
});
Step("Bootable: refuses a non-removable disk", () =>
    Expect<StorageException>(() => media.Create(win!, s.GetDisks().First(d => d.HoldsWindows), BootMode.RawImage,
        BootScheme.MbrBiosAndUefi, "", false, null, null, CancellationToken.None)));

// ------------------------------------------------------------- FAT32 beyond Windows' 32 GB limit
void Chkdsk(ulong offset)
{
    var result = s.CheckVolume(At(offset), false);
    Console.WriteLine($"      chkdsk: {result}");
    if (!result.StartsWith("No errors")) throw new Exception("file system check found problems");
}

void LargeFat32(string label, ulong size, uint cluster)
{
    var d = s.GetDisk(diskNo);
    if (d.Style != PartitionStyle.Raw) s.CleanDisk(d);
    s.InitializeDisk(s.GetDisk(diskNo), PartitionStyle.Gpt);
    var p = s.CreatePartition(s.GetDisk(diskNo), 32 * MB, size);
    if (cluster == 0) s.FormatVolume(p, "FAT32", label, 0, false);
    else s.FormatLargeFat32(p, label, cluster);
    s.AutoAssignDriveLetter(At(32 * MB));
    var h = WriteSample(32 * MB);
    var part = At(32 * MB);
    var info = new DriveInfo(Root(32 * MB));
    Console.WriteLine($"      {info.DriveFormat} '{info.VolumeLabel}' {Format.Bytes((ulong)info.TotalSize)}, {Format.Bytes((ulong)info.TotalFreeSpace)} free");
    if (info.DriveFormat != "FAT32") throw new Exception($"mounted as {info.DriveFormat}");
    if ((ulong)info.TotalSize < size * 99 / 100) throw new Exception("volume smaller than the partition");
    if (!string.Equals(info.VolumeLabel, label, StringComparison.OrdinalIgnoreCase)) throw new Exception($"label '{info.VolumeLabel}'");
    Verify(32 * MB, h, label);
    Chkdsk(32 * MB);
}

Step("FAT32 on 40 GB (Windows refuses; default 32 KB clusters)", () => LargeFat32("3DS", 40UL * 1024 * MB, 0));
Step("FAT32 on 40 GB with 64 KB clusters", () => LargeFat32("BIG64", 40UL * 1024 * MB, 65536));
Step("FAT32 own formatter on 1 GB with 4 KB clusters", () => LargeFat32("SMALL", 1024 * MB, 4096));
Step("FAT32 with too few clusters is refused (1 GB, 64 KB)", () =>
    Expect<StorageException>(() => s.FormatLargeFat32(At(32 * MB), "X", 65536)));
Step("MBR: FAT32 over the whole 48 GB disk", () =>
{
    s.CleanDisk(s.GetDisk(diskNo));
    s.InitializeDisk(s.GetDisk(diskNo), PartitionStyle.Mbr);
    var free = s.GetDisk(diskNo).Segments.Single(x => x.IsFree);
    var p = s.CreatePartition(s.GetDisk(diskNo), free.Offset, free.Size);
    s.FormatVolume(p, "FAT32", "WHOLE", 0, false);
    s.AutoAssignDriveLetter(At(free.Offset));
    var h = WriteSample(free.Offset);
    Verify(free.Offset, h, "whole");
    if (At(free.Offset).MbrType != 0x0C) throw new Exception($"MBR type 0x{At(free.Offset).MbrType:X2}");
    Chkdsk(free.Offset);
});

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;


sealed class SyncLog : IProgress<string> { public void Report(string v) => Console.WriteLine("      > " + v); }
