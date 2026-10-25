using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PartitionDesk.Core;
using PartitionDesk.Dialogs;

namespace PartitionDesk;

public partial class MainWindow : Window
{
    readonly StorageService _storage = new();
    List<DiskInfo> _disks = new();
    DiskInfo? Disk => DiskList.SelectedItem as DiskInfo;
    Segment? Selected => SegmentGrid.SelectedItem as Segment;
    PartitionInfo? Part => Selected?.Partition;

    static readonly string[] FileSystems = { "NTFS", "exFAT", "FAT32", "ReFS" };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        KeyDown += async (_, e) => { if (e.Key == Key.F5) await RefreshAsync(); };
        // Screenshot tools hook PrintScreen from a non-elevated process, and Windows keeps those
        // hooks from seeing keys typed into this elevated window. Open the snipping overlay ourselves.
        PreviewKeyUp += (_, e) =>
        {
            if (e.Key != Key.Snapshot) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
            catch { /* Snipping Tool not installed */ }
        };
        SegmentGrid.ContextMenu = BuildRowMenu();
        UpdateCommands();
    }

    // ---------------------------------------------------------------- loading and display

    async Task RefreshAsync(int? keepDisk = null, ulong? keepOffset = null)
    {
        keepDisk ??= Disk?.Number;
        keepOffset ??= Selected?.Offset;
        StatusText.Text = "Loading disks…";
        IsEnabled = false;
        try
        {
            _disks = await Task.Run(() => { _storage.RefreshCache(); return _storage.GetDisks(); });
            DiskList.ItemsSource = _disks;
            DiskList.SelectedItem = _disks.FirstOrDefault(d => d.Number == keepDisk) ?? _disks.FirstOrDefault();
            if (Disk != null && keepOffset != null)
                SegmentGrid.SelectedItem = Disk.Segments.FirstOrDefault(s => s.Offset == keepOffset);
            StatusText.Text = $"{_disks.Count} disks found.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not read disks: " + ex.Message;
        }
        finally { IsEnabled = true; }
    }

    void DiskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var d = Disk;
        DiskTitle.Text = d == null ? "" : $"{d.Title} — {d.FriendlyName}";
        DiskSubtitle.Text = d == null ? "" : $"{Format.Bytes(d.Size)} · {d.StyleText} · {d.BusType} · {d.LogicalSectorSize}-byte sectors"
            + (d.HoldsWindows ? " · Windows runs from this disk" : "")
            + (d.IsOffline ? " · Offline" : "") + (d.IsReadOnly ? " · Read-only" : "");
        SegmentGrid.ItemsSource = d?.Segments;
        DrawMap();
        UpdateCommands();
    }

    void SegmentGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DrawMap();
        UpdateCommands();
    }

    void SegmentGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected?.IsFree == true) Create_Click(sender, e);
        else if (Part != null) Properties_Click(sender, e);
    }

    void DrawMap()
    {
        DiskMap.Children.Clear();
        DiskMap.ColumnDefinitions.Clear();
        var d = Disk;
        if (d == null) return;
        if (d.Style == PartitionStyle.Raw || d.Segments.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = d.Style == PartitionStyle.Raw ? "This disk is not initialized. Use Disk ▾ → Initialize." : "No partitions.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DiskMap.Children.Add(empty);
            return;
        }
        double total = d.Segments.Sum(s => (double)s.Size);
        for (int i = 0; i < d.Segments.Count; i++)
        {
            var s = d.Segments[i];
            // Small partitions still get a readable block.
            double weight = Math.Max(s.Size / total, 0.05);
            DiskMap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star), MinWidth = 36 });
            var block = MakeBlock(s, s == Selected);
            Grid.SetColumn(block, i);
            DiskMap.Children.Add(block);
        }
    }

    FrameworkElement MakeBlock(Segment s, bool selected)
    {
        var color = ColorFor(s);
        var text = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
        text.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = s.SizeText, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new TextBlock { Text = s.IsFree ? "" : (s.FileSystem.Length > 0 ? s.FileSystem : s.Type), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(detail);

        var used = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var body = new Grid { ClipToBounds = true };
        body.Children.Add(used);
        body.Children.Add(text);
        body.SizeChanged += (_, _) => used.Width = body.ActualWidth * s.UsedFraction;

        var dock = new DockPanel();
        var stripe = new Border { Height = 6, Background = new SolidColorBrush(color) };
        DockPanel.SetDock(stripe, Dock.Top);
        dock.Children.Add(stripe);
        dock.Children.Add(body);

        var border = new Border
        {
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Child = dock,
            Cursor = Cursors.Hand,
            ToolTip = s.IsFree ? $"Unallocated · {s.SizeText}" : $"{s.Name}\n{s.Type} · {s.FileSystem}\n{s.SizeText} at {s.OffsetText}\n{s.Flags}",
            ClipToBounds = true,
        };
        border.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Line");
        border.SetResourceReference(Border.BackgroundProperty, s.IsFree ? "FreeSpace" : "Surface");
        border.MouseLeftButtonDown += (_, e) =>
        {
            SegmentGrid.SelectedItem = s;
            if (e.ClickCount == 2) SegmentGrid_MouseDoubleClick(border, e);
        };
        return border;
    }

    static Color ColorFor(Segment s)
    {
        if (s.IsFree) return Color.FromRgb(0xCB, 0xD5, 0xE1);
        var p = s.Partition!;
        if (p.IsProtectedType) return Color.FromRgb(0xF5, 0x9E, 0x0B);
        return p.FileSystem.ToUpperInvariant() switch
        {
            "NTFS" => Color.FromRgb(0x3B, 0x82, 0xF6),
            "FAT32" or "FAT" or "EXFAT" => Color.FromRgb(0x10, 0xB9, 0x81),
            "REFS" => Color.FromRgb(0x8B, 0x5C, 0xF6),
            _ => Color.FromRgb(0x94, 0xA3, 0xB8),
        };
    }

    void UpdateCommands()
    {
        var d = Disk;
        var s = Selected;
        var p = Part;
        bool part = p != null && !p.IsExtended;
        bool vol = part && p!.VolumePath.Length > 0;
        bool usable = d != null && !d.IsOffline;

        BtnCreate.IsEnabled = usable && s?.IsFree == true && d!.Style != PartitionStyle.Raw;
        BtnFormat.IsEnabled = usable && vol && !p!.IsInUseBySystem;
        BtnDelete.IsEnabled = usable && p != null && !p.IsInUseBySystem;
        BtnForceDelete.IsEnabled = d != null && p != null && !p.IsInUseBySystem;
        BtnResize.IsEnabled = usable && part;
        BtnMove.IsEnabled = usable && part && !p!.IsLogical && !p.IsInUseBySystem;
        BtnClone.IsEnabled = usable && part && !p!.IsInUseBySystem;
        BtnLetter.IsEnabled = usable && vol;
        BtnLabel.IsEnabled = usable && vol && p!.FileSystem.Length > 0;
        BtnPartition.IsEnabled = usable && part;
        BtnDisk.IsEnabled = d != null;

        MiCheck.IsEnabled = MiRepair.IsEnabled = vol && p!.FileSystem.Length > 0;
        MiActive.IsEnabled = part && d!.Style == PartitionStyle.Mbr && !p!.IsLogical;
        MiActive.Header = p?.IsActive == true ? "Mark as inactive" : "Mark as active (MBR boot)";
        MiHidden.Header = p?.IsHidden == true ? "Unhide (allow drive letters)" : "Hide (no drive letter)";
        MiReadOnly.Header = p?.IsReadOnly == true ? "Clear read-only" : "Make read-only";

        bool raw = d?.Style == PartitionStyle.Raw;
        bool empty = d != null && d.Partitions.Count == 0;
        bool winDisk = d?.HoldsWindows == true;
        MiInitGpt.IsEnabled = MiInitMbr.IsEnabled = raw;
        MiToGpt.IsEnabled = !raw && empty && d!.Style == PartitionStyle.Mbr;
        MiToMbr.IsEnabled = !raw && empty && d!.Style == PartitionStyle.Gpt;
        MiOnline.IsEnabled = !winDisk;
        MiOnline.Header = d?.IsOffline == true ? "Bring disk online" : "Take disk offline";
        MiDiskReadOnly.IsEnabled = !winDisk;
        MiDiskReadOnly.Header = d?.IsReadOnly == true ? "Clear disk read-only" : "Make disk read-only";
        MiClean.IsEnabled = d != null && !winDisk && !raw;
    }

    ContextMenu BuildRowMenu()
    {
        var menu = new ContextMenu();
        void Add(string header, RoutedEventHandler click, Func<bool> enabled)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += click;
            menu.Items.Add(mi);
            menu.Opened += (_, _) => mi.IsEnabled = enabled();
        }
        Add("New partition…", Create_Click, () => BtnCreate.IsEnabled);
        Add("Format…", Format_Click, () => BtnFormat.IsEnabled);
        Add("Delete", Delete_Click, () => BtnDelete.IsEnabled);
        Add("Force delete…", ForceDelete_Click, () => BtnForceDelete.IsEnabled);
        menu.Items.Add(new Separator());
        Add("Resize…", Resize_Click, () => BtnResize.IsEnabled);
        Add("Move…", Move_Click, () => BtnMove.IsEnabled);
        Add("Clone…", Clone_Click, () => BtnClone.IsEnabled);
        menu.Items.Add(new Separator());
        Add("Change drive letter…", Letter_Click, () => BtnLetter.IsEnabled);
        Add("Change label…", Label_Click, () => BtnLabel.IsEnabled);
        Add("Properties", Properties_Click, () => Part != null);
        return menu;
    }

    void ShowMenu_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        b.ContextMenu.PlacementTarget = b;
        b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        b.ContextMenu.IsOpen = true;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ---------------------------------------------------------------- helpers

    /// <summary>Runs an operation with a progress dialog, reports the outcome and reloads.</summary>
    async Task RunAsync(string title, Action<IProgress<CopyProgress>, IProgress<string>, CancellationToken> work,
        bool cancellable = false, string? done = null, int? selectDisk = null, ulong? selectOffset = null)
    {
        var error = ProgressWindow.Run(this, title, cancellable, work);
        if (error is OperationCanceledException)
            StatusText.Text = $"{title}: cancelled.";
        else if (error != null)
        {
            StatusText.Text = $"{title} failed.";
            MessageBox.Show(this, error.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
            StatusText.Text = done ?? $"{title}: done.";
        await RefreshAsync(selectDisk, selectOffset);
    }

    bool Confirm(string text, string title) =>
        MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    static List<char> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !used.Contains(c)).ToList();
    }

    static ulong ToMb(ulong bytes) => bytes / Format.MiB;

    // ---------------------------------------------------------------- partition operations

    async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Disk is not { } d || Selected is not { IsFree: true } free) return;
        ulong freeMb = ToMb(free.Size);
        var dlg = new FormDialog(this, "New partition", "Create");
        dlg.AddNote($"{Format.Bytes(free.Size)} of unallocated space on {d.Title}, starting at {free.OffsetText}.");
        var size = dlg.AddMegabytes("Size", freeMb, 1, freeMb);
        var before = dlg.AddMegabytes("Free space before the partition", 0, 0, freeMb - 1);
        var fs = dlg.AddCombo("File system", FileSystems.Append("None (leave unformatted)"), "NTFS");
        var label = dlg.AddText("Volume label", "New Volume", 32);
        var letters = new List<string> { "Automatic", "None" };
        letters.AddRange(FreeLetters().Select(c => $"{c}:"));
        var letter = dlg.AddCombo("Drive letter", letters, "Automatic");
        var quick = dlg.AddCheck("Quick format", true);
        dlg.AddValidator(() => size() + before() > freeMb ? "The partition does not fit in the free space at that position." : null);
        dlg.AddValidator(() => fs() == "FAT32" && size() > 32 * 1024 ? "Windows can only format FAT32 up to 32 GB. Choose exFAT or NTFS." : null);
        if (!dlg.Run()) return;

        ulong offset = free.Offset + before() * Format.MiB;
        ulong bytes = size() * Format.MiB;
        string fsName = fs(), labelText = label(), letterChoice = letter();
        bool quickFormat = quick();
        await RunAsync("New partition", (_, log, _) =>
        {
            log.Report("Creating the partition");
            var p = _storage.CreatePartition(d, offset, bytes);
            if (!fsName.StartsWith("None"))
            {
                log.Report($"Formatting as {fsName}");
                _storage.FormatVolume(p, fsName, labelText, 0, !quickFormat);
            }
            if (letterChoice == "Automatic")
            {
                log.Report("Assigning a drive letter");
                _storage.AutoAssignDriveLetter(p);
            }
            else if (letterChoice != "None")
            {
                log.Report($"Assigning {letterChoice}");
                _storage.SetDriveLetter(p, letterChoice[0]);
            }
        }, done: "Partition created.", selectOffset: offset);
    }

    async void Format_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        var dlg = new FormDialog(this, $"Format {p.DisplayName}", "Format", danger: true);
        dlg.AddNote($"Formatting erases everything on {p.DisplayName} ({Format.Bytes(p.Size)}).", warning: true);
        var fs = dlg.AddCombo("File system", FileSystems, FileSystems.Contains(p.FileSystem) ? p.FileSystem : "NTFS");
        var label = dlg.AddText("Volume label", p.Label, 32);
        var clusters = dlg.AddCombo("Allocation unit size", new uint[] { 0, 4096, 8192, 16384, 32768, 65536 }, 0u,
            v => v == 0 ? "Default" : $"{v / 1024} KB");
        var quick = dlg.AddCheck("Quick format", true);
        dlg.AddValidator(() => fs() == "FAT32" && p.Size > 32 * Format.GiB ? "Windows can only format FAT32 up to 32 GB." : null);
        if (!dlg.Run()) return;
        string f = fs(), l = label();
        uint c = clusters();
        bool q = quick();
        await RunAsync("Format", (_, log, _) =>
        {
            log.Report($"Formatting as {f}");
            _storage.FormatVolume(p, f, l, c, !q);
        }, done: $"{p.DisplayName} formatted as {f}.");
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        if (p.IsProtectedType &&
            MessageBox.Show(this, $"{p.DisplayName} is a protected {p.TypeName} partition. Windows normally refuses to delete it.\n\nUse Force delete instead?",
                "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            ForceDelete_Click(sender, e);
            return;
        }
        if (!Confirm($"Delete {p.DisplayName} ({Format.Bytes(p.Size)}, {p.TypeName})?\n\nAll data on it will be lost.", "Delete partition")) return;
        await RunAsync("Delete", (_, log, _) =>
        {
            log.Report("Deleting the partition");
            _storage.Delete(p);
        }, done: $"{p.DisplayName} deleted.");
    }

    async void ForceDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p || Disk is not { } d) return;
        var dlg = new FormDialog(this, "Force delete", "Force delete", danger: true);
        dlg.AddNote($"{p.DisplayName} · {p.TypeName} · {Format.Bytes(p.Size)} on {d.Title}");
        var warn = "Force delete removes the partition even when Windows protects it: it clears the disk's read-only flag, " +
                   "force-dismounts the volume (closing any open files) and removes the entry from the partition table. All data on it is lost.";
        if (p.IsProtectedType && d.HoldsWindows)
            warn += $"\n\nThis {p.TypeName} partition is on the Windows disk. Deleting it can stop Windows from starting or remove its recovery tools.";
        dlg.AddNote(warn, warning: true);
        dlg.RequireTyping("DELETE");
        if (!dlg.Run()) return;
        await RunAsync("Force delete", (_, log, _) => _storage.ForceDelete(p, log), done: $"{p.DisplayName} force-deleted.");
    }

    async void Resize_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        (ulong Min, ulong Max) range;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            range = await Task.Run(() => _storage.GetSupportedSize(p));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "This partition cannot be resized.\n\n" + ex.Message, "Resize", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        ulong min = Format.AlignUp(range.Min, Format.MiB) / Format.MiB, max = range.Max / Format.MiB;
        if (max <= min)
        {
            MessageBox.Show(this, $"{p.DisplayName} cannot be resized right now (the supported range is a single size). " +
                "To extend, there must be unallocated space directly after it.", "Resize", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new FormDialog(this, $"Resize {p.DisplayName}", "Resize");
        dlg.AddNote($"Current size: {Format.Bytes(p.Size)}. The file system is shrunk or extended along with the partition. " +
                    "Extending uses unallocated space directly after the partition; to grow at the start, move the partition first.");
        var size = dlg.AddMegabytes("New size", ToMb(p.Size), min, max);
        if (!dlg.Run()) return;
        ulong bytes = size() * Format.MiB;
        if (bytes == Format.AlignDown(p.Size, Format.MiB)) return;
        await RunAsync("Resize", (_, log, _) =>
        {
            log.Report($"Resizing to {Format.Bytes(bytes)}");
            _storage.Resize(p, bytes);
        }, done: $"{p.DisplayName} resized to {Format.Bytes(bytes)}.");
    }

    async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p || Disk is not { } d) return;
        var others = d.Partitions.Where(x => x != p && !x.IsExtended).ToList();
        ulong lo = Format.AlignUp(others.Where(x => x.End <= p.Offset).Select(x => x.End).DefaultIfEmpty(d.UsableStart).Max(), Format.MiB);
        lo = Math.Max(lo, Format.AlignUp(d.UsableStart, Format.MiB));
        ulong next = others.Where(x => x.Offset >= p.End).Select(x => x.Offset).DefaultIfEmpty(d.UsableEnd).Min();
        ulong hi = next >= p.Size ? Format.AlignDown(next - p.Size, Format.MiB) : 0;
        if (hi <= lo)
        {
            MessageBox.Show(this, "There is no unallocated space next to this partition to move it into.", "Move", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ulong current = p.Offset >= lo ? (p.Offset - lo) / Format.MiB : 0;
        ulong span = (hi - lo) / Format.MiB;

        var dlg = new FormDialog(this, $"Move {p.DisplayName}", "Move", danger: true);
        dlg.AddNote($"{p.DisplayName} ({Format.Bytes(p.Size)}) can move within {Format.Bytes(hi - lo + p.Size)} of space. " +
                    "Its data is copied sector by sector, which can take a long time.");
        dlg.AddNote("Back up important data first. A power cut or crash while the partition is being moved will corrupt it. " +
                    "The volume is dismounted during the move; programs using it lose access. " +
                    "If this is a boot partition of another operating system, its boot loader may need repair afterwards.", warning: true);
        var before = dlg.AddMegabytes("Free space before the partition", current, 0, span);
        dlg.AddValidator(() => before() == current ? "Choose a new position." : null);
        if (!dlg.Run()) return;
        ulong newOffset = lo + before() * Format.MiB;
        await RunAsync("Move", (copy, log, _) => _storage.Move(p, newOffset, copy, log),
            done: $"{p.DisplayName} moved to {Format.Bytes(newOffset)}.", selectOffset: newOffset);
    }

    async void Clone_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p || Disk is not { } d) return;
        var targets = _disks.SelectMany(x => x.Segments)
            .Where(s => s.IsFree && s.Size >= p.Size && !s.Disk.IsOffline && !s.Disk.IsReadOnly
                        && s.Disk.LogicalSectorSize == d.LogicalSectorSize)
            .ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, $"No disk has {Format.Bytes(p.Size)} of contiguous unallocated space (with the same sector size) for a copy.",
                "Clone", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new FormDialog(this, $"Clone {p.DisplayName}", "Clone");
        dlg.AddNote($"Copies {p.DisplayName} ({Format.Bytes(p.Size)}, {p.TypeName}) sector by sector into unallocated space. " +
                    "The source volume is locked while it is copied, so programs cannot use it in the meantime.");
        var target = dlg.AddCombo("Copy to", targets, targets[0],
            s => $"{s.Disk.Title} ({s.Disk.FriendlyName}) · {Format.Bytes(s.Size)} free at {s.OffsetText}");
        ulong maxMb = ToMb(targets.Max(s => s.Size));
        var size = dlg.AddMegabytes("Size of the copy (larger sizes extend NTFS/ReFS)", ToMb(Format.AlignUp(p.Size, Format.MiB)),
            ToMb(Format.AlignUp(p.Size, Format.MiB)), maxMb);
        var letter = dlg.AddCheck("Give the copy a drive letter", true);
        dlg.AddValidator(() => size() * Format.MiB > target().Size ? "The copy does not fit in that free space." : null);
        if (!dlg.Run()) return;
        var t = target();
        ulong bytes = Math.Max(p.Size, size() * Format.MiB);
        bool assign = letter();
        await RunAsync("Clone", (copy, log, ct) => _storage.Clone(p, t.Disk, t.Offset, bytes, assign, copy, log, ct),
            cancellable: true, done: $"{p.DisplayName} cloned to {t.Disk.Title}.", selectDisk: t.Disk.Number, selectOffset: t.Offset);
    }

    async void Letter_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        var options = new List<string> { "None" };
        if (p.DriveLetter != '\0') options.Add(p.LetterText);
        options.AddRange(FreeLetters().Select(c => $"{c}:"));
        var dlg = new FormDialog(this, "Drive letter", "Apply");
        var choice = dlg.AddCombo($"Drive letter for {p.DisplayName}", options, p.DriveLetter != '\0' ? p.LetterText : "None");
        if (!dlg.Run()) return;
        var c = choice();
        if (c == p.LetterText || (c == "None" && p.DriveLetter == '\0')) return;
        await RunAsync("Drive letter", (_, _, _) => _storage.SetDriveLetter(p, c == "None" ? null : c[0]),
            done: c == "None" ? "Drive letter removed." : $"Drive letter set to {c}.");
    }

    async void Label_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        var dlg = new FormDialog(this, "Volume label", "Apply");
        var label = dlg.AddText("Label", p.Label, 32);
        if (!dlg.Run()) return;
        var l = label();
        await RunAsync("Label", (_, _, _) => _storage.SetLabel(p, l), done: "Label changed.");
    }

    void Check_Click(object sender, RoutedEventArgs e) => CheckVolume(false);
    void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm("Check and repair takes the volume offline while it is repaired. Programs using it lose access.\n\nContinue?", "Repair"))
            CheckVolume(true);
    }

    async void CheckVolume(bool fix)
    {
        if (Part is not { } p) return;
        string result = "";
        var error = ProgressWindow.Run(this, fix ? "Repair" : "Check", false, (_, log, _) =>
        {
            log.Report(fix ? "Checking and repairing the file system" : "Checking the file system");
            result = _storage.CheckVolume(p, fix);
        });
        MessageBox.Show(this, error?.Message ?? result, $"{p.DisplayName}", MessageBoxButton.OK,
            error != null ? MessageBoxImage.Error : MessageBoxImage.Information);
        await RefreshAsync();
    }

    async void Active_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        bool on = !p.IsActive;
        await RunAsync("Active flag", (_, _, _) => _storage.SetAttributes(p, active: on));
    }

    async void Hidden_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        bool on = !p.IsHidden;
        await RunAsync("Hidden flag", (_, _, _) =>
        {
            if (on && p.DriveLetter != '\0') _storage.SetDriveLetter(p, null);
            _storage.SetAttributes(p, hidden: on);
        });
    }

    async void ReadOnly_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        bool on = !p.IsReadOnly;
        await RunAsync("Read-only flag", (_, _, _) => _storage.SetAttributes(p, readOnly: on));
    }

    async void Type_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p || Disk is not { } d) return;
        var dlg = new FormDialog(this, "Partition type", "Apply", danger: true);
        dlg.AddNote("Changing the type changes how Windows treats the partition. Setting an EFI, recovery or OEM partition " +
                    "to Basic data removes its protection, so it gets a drive letter and can be deleted or formatted normally.", warning: true);
        Func<string> gpt = () => "";
        Func<string> mbr = () => "";
        if (d.Style == PartitionStyle.Gpt)
        {
            var types = new (string Name, string Guid)[]
            {
                ("Basic data", PartitionTypes.BasicData), ("EFI System", PartitionTypes.Efi),
                ("Microsoft Reserved", PartitionTypes.Msr), ("Recovery", PartitionTypes.Recovery),
            }.ToList();
            var current = types.FirstOrDefault(t => string.Equals(t.Guid, p.GptType, StringComparison.OrdinalIgnoreCase));
            var pick = dlg.AddCombo("Type", types, current.Guid == null ? types[0] : current, t => $"{t.Name}  {t.Guid}");
            gpt = () => pick().Guid;
        }
        else
        {
            var hex = dlg.AddText("MBR type (hex, e.g. 07 = NTFS/exFAT, 0C = FAT32, 27 = recovery)", p.MbrType.ToString("X2"), 2);
            mbr = hex;
            dlg.AddValidator(() => byte.TryParse(hex(), System.Globalization.NumberStyles.HexNumber, null, out var b) && b != 0
                ? null : "Enter a hexadecimal type between 01 and FF.");
        }
        if (!dlg.Run()) return;
        string g = gpt(), m = mbr();
        await RunAsync("Partition type", (_, _, _) =>
        {
            if (g.Length > 0) _storage.SetAttributes(p, gptType: g);
            else _storage.SetAttributes(p, mbrType: Convert.ToUInt16(m, 16));
        }, done: "Partition type changed.");
    }

    void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Part is not { } p) return;
        var lines = new[]
        {
            $"Disk {p.DiskNumber}, partition {p.PartitionNumber}",
            $"Type: {p.TypeName}" + (p.GptType.Length > 0 ? $"  {p.GptType}" : $"  (0x{p.MbrType:X2})"),
            $"Partition GUID: {p.Guid}",
            $"Starts at: {p.Offset:N0} bytes ({Format.Bytes(p.Offset)})",
            $"Size: {p.Size:N0} bytes ({Format.Bytes(p.Size)})",
            $"File system: {(p.FileSystem.Length > 0 ? p.FileSystem : "none / unknown")}",
            $"Label: {p.Label}",
            $"Free: {(p.HasVolumeSize ? Format.Bytes(p.FreeSpace) : "n/a")}",
            $"Access paths:\n  " + string.Join("\n  ", p.AccessPaths),
            $"Flags: {(p.FlagsText.Length > 0 ? p.FlagsText : "none")}",
        };
        MessageBox.Show(this, string.Join("\n", lines), p.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------------------------------------------------------------- bootable media

    async void Bootable_Click(object sender, RoutedEventArgs e)
    {
        var targets = _disks.Where(BootableMediaService.IsSuitableTarget).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Connect a USB drive or memory card first, then click Refresh.", "Bootable USB",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var open = new Microsoft.Win32.OpenFileDialog { Title = "Choose a disk image", Filter = "Disk images (*.iso;*.img)|*.iso;*.img|All files|*.*" };
        if (open.ShowDialog(this) != true) return;

        var media = new BootableMediaService(_storage);
        IsoInfo? iso = null;
        bool isIso = open.FileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        var error = ProgressWindow.Run(this, "Reading image", false, (_, log, _) =>
        {
            log.Report("Inspecting the image");
            iso = isIso ? media.Inspect(open.FileName)
                // .img files are raw disk images: they can only be written as-is.
                : new IsoInfo { Path = open.FileName, Size = (ulong)new FileInfo(open.FileName).Length, IsHybrid = true };
        });
        if (error != null || iso == null)
        {
            MessageBox.Show(this, "This file could not be read as a disk image.\n\n" + error?.Message, "Bootable USB", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dlg = new FormDialog(this, "Create bootable USB", "Start", danger: true);
        dlg.AddNote($"{Path.GetFileName(iso.Path)}\n{(isIso ? iso.Describe() : $"Raw disk image · {Format.Bytes(iso.Size)}")}");
        var preferred = targets.FirstOrDefault(d => d.Number == Disk?.Number) ?? targets[0];
        var target = dlg.AddCombo("Drive", targets, preferred, d => $"{d.Title} · {d.FriendlyName} · {Format.Bytes(d.Size)} · {d.BusType}");

        var modes = new List<BootMode>();
        if (isIso && iso.CanCopyFiles) modes.Add(BootMode.CopyFiles);
        modes.Add(BootMode.RawImage);
        var mode = dlg.AddCombo("Method", modes, modes.Contains(iso.Recommended) ? iso.Recommended : modes[0], m => m switch
        {
            BootMode.CopyFiles => "Copy files to a FAT32 drive" + (iso.Recommended == m ? " (recommended)" : ""),
            _ => "Write raw image (byte for byte)" + (iso.Recommended == m ? " (recommended)" : ""),
        });
        var scheme = dlg.AddCombo("Partition scheme (when copying files)", new[] { BootScheme.MbrBiosAndUefi, BootScheme.GptUefiOnly },
            BootScheme.MbrBiosAndUefi, s => s == BootScheme.MbrBiosAndUefi ? "MBR — boots on BIOS and UEFI PCs" : "GPT — UEFI PCs only");
        var label = dlg.AddText("Volume label (when copying files)", iso.Label.Length > 0 ? iso.Label : "BOOT", 11);
        var verify = dlg.AddCheck("Verify after writing (raw image)", true);
        if (iso.LargeFiles.Any(IsoInfo.IsSplittableWim))
            dlg.AddNote("The Windows install image is larger than 4 GB; it will be split into parts so it fits on FAT32.");
        dlg.AddNote("Everything on the selected drive will be erased.", warning: true);
        dlg.RequireTyping("ERASE");
        dlg.AddValidator(() => mode() == BootMode.RawImage && iso.Size > target().Size ? "The image is larger than the drive." : null);
        if (!dlg.Run()) return;

        var t = target();
        var m = mode();
        var sc = scheme();
        var l = label();
        bool v = verify();
        await RunAsync("Bootable USB", (copy, log, ct) => media.Create(iso, t, m, sc, l, v, copy, log, ct),
            cancellable: true, done: $"{t.Title} is ready to boot.", selectDisk: t.Number);
    }

    // ---------------------------------------------------------------- disk operations

    async void InitGpt_Click(object sender, RoutedEventArgs e) => await Initialize(PartitionStyle.Gpt);
    async void InitMbr_Click(object sender, RoutedEventArgs e) => await Initialize(PartitionStyle.Mbr);

    async Task Initialize(PartitionStyle style)
    {
        if (Disk is not { } d) return;
        if (!Confirm($"Initialize {d.Title} ({d.FriendlyName}) as {style.ToString().ToUpperInvariant()}?", "Initialize disk")) return;
        await RunAsync("Initialize", (_, _, _) => _storage.InitializeDisk(d, style));
    }

    async void ToGpt_Click(object sender, RoutedEventArgs e) => await ConvertDisk(PartitionStyle.Gpt);
    async void ToMbr_Click(object sender, RoutedEventArgs e) => await ConvertDisk(PartitionStyle.Mbr);

    async Task ConvertDisk(PartitionStyle style)
    {
        if (Disk is not { } d) return;
        if (!Confirm($"Convert {d.Title} to {style.ToString().ToUpperInvariant()}?", "Convert disk")) return;
        await RunAsync("Convert", (_, _, _) => _storage.ConvertDisk(d, style));
    }

    async void Online_Click(object sender, RoutedEventArgs e)
    {
        if (Disk is not { } d) return;
        bool online = d.IsOffline;
        if (!online && !Confirm($"Take {d.Title} offline? Its volumes become unavailable.", "Offline")) return;
        await RunAsync(online ? "Online" : "Offline", (_, _, _) => _storage.SetDiskOnline(d, online));
    }

    async void DiskReadOnly_Click(object sender, RoutedEventArgs e)
    {
        if (Disk is not { } d) return;
        bool on = !d.IsReadOnly;
        await RunAsync("Disk read-only", (_, _, _) => _storage.SetDiskReadOnly(d, on));
    }

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (Disk is not { } d) return;
        var dlg = new FormDialog(this, "Clean disk", "Clean", danger: true);
        dlg.AddNote($"{d.Title} · {d.FriendlyName} · {Format.Bytes(d.Size)}");
        dlg.AddNote($"Removes all {d.Partitions.Count} partitions, including protected OEM and recovery partitions, " +
                    "and leaves the disk uninitialized. All data on the disk is lost.", warning: true);
        dlg.RequireTyping("CLEAN");
        if (!dlg.Run()) return;
        await RunAsync("Clean disk", (_, log, _) =>
        {
            log.Report("Removing all partitions");
            _storage.CleanDisk(d);
        }, done: $"{d.Title} cleaned.");
    }
}
