# PartitionDesk

A Windows partition manager. It runs as administrator.

## Features

- **Create, format, delete and resize** partitions (NTFS, exFAT, FAT32, ReFS). Resizing shrinks or extends the file system along with the partition.
- **Force delete** partitions that Windows protects (EFI, recovery, OEM, Microsoft Reserved). It clears the disk's read-only flag, force-dismounts the volume and removes the partition table entry.
- **Move** a partition within the free space around it. Data is copied sector by sector, safely even when the old and new positions overlap, and the boot-sector start fields of NTFS, FAT and exFAT are updated.
- **Clone** a partition into free space on any disk, optionally growing the copy.
- **Bootable USB** from an ISO or IMG file:
  - *Copy files* to a FAT32 drive that boots on BIOS and UEFI PCs. Windows install images over 4 GB are split with DISM.
  - *Write raw image* for hybrid (Linux-style) ISOs, with verification.
- Drive letters, labels, file system check and repair, active/hidden/read-only flags, and partition type changes.
- Disk tools: initialize, convert between MBR and GPT, online/offline, read-only and clean.
- Light and dark mode follow the Windows setting.

The disk Windows runs from is guarded. Its active partitions cannot be deleted, moved or formatted, and whole-disk operations on it are refused.

## Build

Requires the .NET 10 SDK and Inno Setup 6.

```powershell
./build-installer.ps1 -Version 1.0.0   # creates dist\PartitionDesk-Setup-1.0.0.exe
```

## Self-test

`SelfTest` runs every operation against a temporary 1 GB virtual disk (VHDX). It refuses to touch any other disk.

```powershell
SelfTest\make-test-iso.ps1             # builds small test ISOs
SelfTest\run-selftest.ps1              # elevated; add -Only boot for just the bootable-media tests
```
