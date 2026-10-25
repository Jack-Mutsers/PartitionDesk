using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PartitionDesk.Core;

public enum BootMode { CopyFiles, RawImage }
public enum BootScheme { MbrBiosAndUefi, GptUefiOnly }

public sealed class IsoInfo
{
    public string Path { get; init; } = "";
    public ulong Size { get; init; }
    public string Label { get; init; } = "";
    /// <summary>The image carries its own partition table, so it can be written to a disk as-is.</summary>
    public bool IsHybrid { get; init; }
    public bool IsWindows { get; init; }
    public bool HasEfi { get; init; }
    public bool HasBootSect { get; init; }
    /// <summary>Files FAT32 cannot hold (4 GB or more), relative to the image root.</summary>
    public List<string> LargeFiles { get; init; } = new();
    public ulong FilesSize { get; init; }

    public BootMode Recommended => IsWindows || !IsHybrid ? BootMode.CopyFiles : BootMode.RawImage;
    /// <summary>A large file can only be handled by splitting, which works for Windows install images.</summary>
    public bool CanCopyFiles => LargeFiles.All(IsSplittableWim);

    public static bool IsSplittableWim(string relative) =>
        relative.Equals(@"sources\install.wim", StringComparison.OrdinalIgnoreCase);

    public string Describe()
    {
        var kind = IsWindows ? "Windows installation image" : IsHybrid ? "Hybrid image (Linux-style)" : "Bootable image";
        var boot = new List<string>();
        if (HasEfi) boot.Add("UEFI");
        if (HasBootSect || IsHybrid) boot.Add("BIOS");
        return $"{kind} · {Format.Bytes(Size)}" + (boot.Count > 0 ? $" · boots on {string.Join(" + ", boot)}" : " · no boot loader found")
            + (LargeFiles.Count > 0 ? $" · {LargeFiles.Count} file(s) over 4 GB" : "");
    }
}

/// <summary>Writes ISO images to USB drives so a PC can boot from them.</summary>
public sealed class BootableMediaService
{
    const ulong Fat32Limit = 32UL * 1024 * 1024 * 1024;   // Windows formats FAT32 up to 32 GB
    const long FatMaxFile = 4L * 1024 * 1024 * 1024 - 1;
    readonly StorageService _storage;

    public BootableMediaService(StorageService storage) => _storage = storage;

    public static bool IsSuitableTarget(DiskInfo d) =>
        !d.HoldsWindows && d.BusType is "USB" or "SD" or "MMC" or "Virtual disk file";

    public IsoInfo Inspect(string isoPath)
    {
        var fi = new FileInfo(isoPath);
        var head = new byte[512];
        using (var fs = File.OpenRead(isoPath)) fs.ReadExactly(head);
        bool hybrid = head[510] == 0x55 && head[511] == 0xAA &&
                      Enumerable.Range(0, 4).Any(i => head[0x1BE + i * 16 + 4] != 0);

        var mount = IsoMount.Mount(isoPath);
        try
        {
            var root = mount.Root;
            bool Has(string rel) => File.Exists(System.IO.Path.Combine(root, rel));
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
            return new IsoInfo
            {
                Path = isoPath,
                Size = (ulong)fi.Length,
                Label = new DriveInfo(root).VolumeLabel,
                IsHybrid = hybrid,
                IsWindows = Has(@"sources\install.wim") || Has(@"sources\install.esd") || Has(@"sources\boot.wim"),
                HasEfi = Has(@"efi\boot\bootx64.efi") || Has(@"efi\boot\bootaa64.efi") || Has(@"efi\boot\bootia32.efi"),
                HasBootSect = Has(@"boot\bootsect.exe"),
                LargeFiles = files.Where(f => f.Length > FatMaxFile).Select(f => System.IO.Path.GetRelativePath(root, f.FullName)).ToList(),
                FilesSize = (ulong)files.Sum(f => f.Length),
            };
        }
        finally { mount.Dispose(); }
    }

    public void Create(IsoInfo iso, DiskInfo disk, BootMode mode, BootScheme scheme, string label, bool verify,
        IProgress<CopyProgress>? progress, IProgress<string>? log, CancellationToken ct)
    {
        disk = _storage.GetDisk(disk.Number);
        if (!IsSuitableTarget(disk)) throw new StorageException("Bootable media can only be written to USB drives and memory cards.");
        if (disk.IsReadOnly) throw new StorageException("The drive is read-only.");
        if (mode == BootMode.RawImage) WriteRaw(iso, disk, verify, progress, log, ct);
        else CopyFiles(iso, disk, scheme, label, progress, log, ct);
        _storage.RefreshCache();
    }

    // ---------------------------------------------------------------- raw image

    unsafe void WriteRaw(IsoInfo iso, DiskInfo disk, bool verify, IProgress<CopyProgress>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (iso.Size > disk.Size) throw new StorageException($"The image ({Format.Bytes(iso.Size)}) is larger than the drive.");
        Wipe(disk, log);

        const int chunk = 4 * 1024 * 1024;
        ulong total = Format.AlignUp(iso.Size, disk.LogicalSectorSize);
        byte* buf = (byte*)NativeMemory.AlignedAlloc(chunk, 4096);
        try
        {
            using var src = new FileStream(iso.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var dst = Native.Open(Native.PhysicalDrive(disk.Number), write: true, unbuffered: true);
            log?.Report("Writing the image");
            // The first chunk holds the partition table: write it last, so Windows does not
            // pick up the new partitions while the rest is still being written.
            for (ulong pos = chunk; pos < total; pos += chunk)
            {
                ct.ThrowIfCancellationRequested();
                uint n = FillFromIso(src, pos, buf, chunk, total);
                RawCopy.WriteAt(dst, pos, buf, n);
                progress?.Report(new CopyProgress(pos + n, total));
            }
            uint first = FillFromIso(src, 0, buf, chunk, total);
            RawCopy.WriteAt(dst, 0, buf, first);
            progress?.Report(new CopyProgress(total, total));

            if (verify)
            {
                log?.Report("Verifying");
                byte* check = (byte*)NativeMemory.AlignedAlloc(chunk, 4096);
                try
                {
                    for (ulong pos = 0; pos < total; pos += chunk)
                    {
                        ct.ThrowIfCancellationRequested();
                        uint n = FillFromIso(src, pos, buf, chunk, total);
                        RawCopy.ReadAt(dst, pos, check, n);
                        if (!new Span<byte>(buf, (int)n).SequenceEqual(new Span<byte>(check, (int)n)))
                            throw new StorageException($"Verification failed at {Format.Bytes(pos)}: the drive may be faulty.");
                        progress?.Report(new CopyProgress(pos + n, total));
                    }
                }
                finally { NativeMemory.AlignedFree(check); }
            }
            Native.Ioctl(dst, Native.IOCTL_DISK_UPDATE_PROPERTIES);
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    /// <summary>Reads image bytes at <paramref name="pos"/>, zero-padding the end to whole sectors.</summary>
    static unsafe uint FillFromIso(FileStream src, ulong pos, byte* buf, int chunk, ulong total)
    {
        uint n = (uint)Math.Min((ulong)chunk, total - pos);
        var span = new Span<byte>(buf, (int)n);
        src.Position = (long)pos;
        int read = 0;
        while (read < n)
        {
            int r = src.Read(span[read..]);
            if (r == 0) break;
            read += r;
        }
        span[read..].Clear();
        return n;
    }

    void Wipe(DiskInfo disk, IProgress<string>? log)
    {
        if (disk.Style == PartitionStyle.Raw) return;
        log?.Report("Removing existing partitions");
        try { _storage.CleanDisk(disk); }
        catch (StorageException)
        {
            // Some USB sticks refuse a clean; remove partitions one by one instead.
            foreach (var p in _storage.GetDisk(disk.Number).Partitions.OrderByDescending(p => p.Offset))
                _storage.ForceDelete(p);
        }
    }

    // ---------------------------------------------------------------- file copy

    void CopyFiles(IsoInfo iso, DiskInfo disk, BootScheme scheme, string label, IProgress<CopyProgress>? progress,
        IProgress<string>? log, CancellationToken ct)
    {
        if (!iso.CanCopyFiles)
            throw new StorageException($"This image contains files over 4 GB ({string.Join(", ", iso.LargeFiles)}) that FAT32 cannot hold. Use \"Write raw image\" instead.");
        if (!iso.HasEfi && !(scheme == BootScheme.MbrBiosAndUefi && iso.HasBootSect))
            log?.Report("Warning: no boot loader found in the image; the drive may not boot");

        log?.Report("Mounting the image");
        using var mount = IsoMount.Mount(iso.Path);
        ulong partSize = Math.Min(Format.AlignDown(disk.UsableEnd, Format.MiB) - Format.MiB, Fat32Limit);
        if (iso.FilesSize > partSize * 98 / 100)
            throw new StorageException($"The files ({Format.Bytes(iso.FilesSize)}) do not fit on the drive.");

        Wipe(disk, log);
        var style = scheme == BootScheme.GptUefiOnly ? PartitionStyle.Gpt : PartitionStyle.Mbr;
        log?.Report($"Initializing the drive as {(style == PartitionStyle.Gpt ? "GPT" : "MBR")}");
        disk = _storage.GetDisk(disk.Number);
        if (disk.Style == PartitionStyle.Raw) _storage.InitializeDisk(disk, style);
        else if (disk.Style != style) _storage.ConvertDisk(disk, style);
        _storage.RefreshCache();

        log?.Report("Creating a FAT32 partition");
        disk = _storage.GetDisk(disk.Number);
        ulong offset = disk.Segments.FirstOrDefault(s => s.IsFree)?.Offset ?? Format.MiB;
        partSize = Math.Min(partSize, Format.AlignDown(disk.UsableEnd - offset, Format.MiB));
        var part = _storage.CreatePartition(disk, offset, partSize);
        if (style == PartitionStyle.Mbr)
        {
            _storage.SetAttributes(part, mbrType: 0x0C);
            _storage.SetAttributes(At(disk.Number, offset), active: true);
        }
        log?.Report("Formatting");
        var fatLabel = new string(label.ToUpperInvariant().Where(c => c >= ' ' && c < 127 && !"*?.,;:/\\|+=<>[]\"".Contains(c)).Take(11).ToArray());
        _storage.FormatVolume(At(disk.Number, offset), "FAT32", fatLabel, 0, false);
        string target = MountedRoot(disk.Number, offset);

        var files = Directory.EnumerateFiles(mount.Root, "*", SearchOption.AllDirectories).ToList();
        ulong total = (ulong)files.Sum(f => new FileInfo(f).Length), done = 0;
        var buffer = new byte[4 * 1024 * 1024];
        log?.Report("Copying files");
        var toSplit = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(mount.Root, file);
            if (new FileInfo(file).Length > FatMaxFile) { toSplit.Add(rel); continue; }
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                int r;
                while ((r = input.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, r);
                    done += (ulong)r;
                    progress?.Report(new CopyProgress(done, total));
                }
            }
            // ISO files are read-only; the copies should not be.
            File.SetAttributes(dest, FileAttributes.Normal);
        }

        foreach (var rel in toSplit)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"Splitting {rel} into parts FAT32 can hold (this takes a while)");
            var swm = Path.ChangeExtension(Path.Combine(target, rel), ".swm");
            Directory.CreateDirectory(Path.GetDirectoryName(swm)!);
            var (code, output) = Run(Path.Combine(Environment.SystemDirectory, "dism.exe"),
                $"/Split-Image /ImageFile:\"{Path.Combine(mount.Root, rel)}\" /SWMFile:\"{swm}\" /FileSize:3800");
            if (code != 0) throw new StorageException($"Splitting {rel} failed: {LastLines(output)}");
            done += (ulong)new FileInfo(Path.Combine(mount.Root, rel)).Length;
            progress?.Report(new CopyProgress(done, total));
        }

        if (style == PartitionStyle.Mbr && iso.HasBootSect)
        {
            log?.Report("Installing the BIOS boot code");
            var (code, output) = Run(Path.Combine(mount.Root, @"boot\bootsect.exe"), $"/nt60 {target.TrimEnd('\\')} /mbr /force");
            if (code != 0) log?.Report($"Warning: bootsect failed ({LastLines(output)}); the drive will only boot on UEFI PCs");
        }
        log?.Report("Flushing data to the drive");
        using (var vol = Native.Open($@"\\.\{target.TrimEnd('\\')}", write: true))
            Native.FlushFileBuffers(vol);
    }

    PartitionInfo At(int disk, ulong offset) => _storage.GetDisk(disk).Partitions.First(p => p.Offset == offset);

    string MountedRoot(int disk, ulong offset)
    {
        for (int i = 0; i < 40; i++)
        {
            var p = At(disk, offset);
            if (p.DriveLetter != '\0' && Directory.Exists($"{p.DriveLetter}:\\")) return $"{p.DriveLetter}:\\";
            if (p.DriveLetter == '\0' && i % 10 == 0)
                try { _storage.AutoAssignDriveLetter(p); } catch { }
            Thread.Sleep(500);
        }
        throw new StorageException("The new partition did not get a drive letter.");
    }

    internal static (int Code, string Output) Run(string exe, string args, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr.Result);
    }

    static string LastLines(string s) =>
        string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
}

/// <summary>Mounts an ISO with Windows' built-in image support and unmounts it again on dispose.</summary>
internal sealed class IsoMount : IDisposable
{
    readonly string _path;
    readonly bool _mountedByUs;
    public string Root { get; }

    IsoMount(string path, string root, bool mountedByUs) { _path = path; Root = root; _mountedByUs = mountedByUs; }

    const string MountScript =
        "$ErrorActionPreference='Stop';" +
        "$i=Get-DiskImage -ImagePath $env:PD_ISO;" +
        "if(-not $i.Attached){$i=Mount-DiskImage -ImagePath $env:PD_ISO -StorageType ISO -Access ReadOnly -PassThru;'MOUNTED'};" +
        "for($n=0;$n -lt 40;$n++){$l=($i|Get-Volume).DriveLetter;if($l){'LETTER='+$l;break};Start-Sleep -Milliseconds 250}";

    public static IsoMount Mount(string isoPath)
    {
        var (code, output) = BootableMediaService.Run("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{MountScript}\"",
            new() { ["PD_ISO"] = Path.GetFullPath(isoPath) });
        var letterLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("LETTER="));
        bool mounted = output.Contains("MOUNTED");
        if (code != 0 || letterLine == null)
        {
            if (mounted) Unmount(isoPath);
            throw new StorageException($"Could not open the image: {output.Trim()}");
        }
        return new IsoMount(isoPath, $"{letterLine[7]}:\\", mounted);
    }

    static void Unmount(string isoPath) =>
        BootableMediaService.Run("powershell.exe",
            "-NoProfile -NonInteractive -Command \"Dismount-DiskImage -ImagePath $env:PD_ISO | Out-Null\"",
            new() { ["PD_ISO"] = Path.GetFullPath(isoPath) });

    public void Dispose()
    {
        if (_mountedByUs) Unmount(_path);
    }
}
