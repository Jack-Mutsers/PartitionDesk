# Builds two small test images with Windows' IMAPI2 image writer:
#   test-win.iso    - Windows-style layout (efi\boot\bootx64.efi, sources\boot.wim)
#   test-hybrid.iso - the same image with an MBR partition table in its (unused) system area
param([string]$OutDir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$src = Join-Path $env:TEMP "pd-iso-src"
if (Test-Path $src) { Remove-Item $src -Recurse -Force }
foreach ($d in 'efi\boot', 'sources', 'boot\fonts', 'support\deep\er') { New-Item -ItemType Directory -Force (Join-Path $src $d) | Out-Null }
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
function Fill($rel, $size) { $b = New-Object byte[] $size; $rng.GetBytes($b); [IO.File]::WriteAllBytes((Join-Path $src $rel), $b) }
Fill 'efi\boot\bootx64.efi' 1MB
Fill 'sources\boot.wim' 24MB
Fill 'boot\fonts\font.bin' 300KB
Fill 'support\deep\er\file.bin' 5MB
Set-Content (Join-Path $src 'setup.txt') 'PartitionDesk test image'

if (-not ('IsoStream' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.IO; using System.Runtime.InteropServices.ComTypes;
public static class IsoStream {
    public static void Save(object comStream, string path) {
        var s = (IStream)comStream; var buf = new byte[1 << 20];
        using (var f = File.Create(path)) {
            while (true) {
                var readPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
                try { s.Read(buf, buf.Length, readPtr); int n = System.Runtime.InteropServices.Marshal.ReadInt32(readPtr); if (n <= 0) break; f.Write(buf, 0, n); }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(readPtr); }
            }
        }
    }
}
'@
}
$fs = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$fs.FileSystemsToCreate = 7      # ISO9660 + Joliet + UDF
$fs.VolumeName = 'PDTEST'
$fs.Root.AddTree($src, $false)
$win = Join-Path $OutDir 'test-win.iso'
[IsoStream]::Save($fs.CreateResultImage().ImageStream, $win)

$hybrid = Join-Path $OutDir 'test-hybrid.iso'
$bytes = [IO.File]::ReadAllBytes($win)
$bytes[0x1BE] = 0x80; $bytes[0x1BE + 4] = 0x17     # one bootable partition entry (type 0x17)
[BitConverter]::GetBytes([uint32]0).CopyTo($bytes, 0x1BE + 8)
[BitConverter]::GetBytes([uint32]($bytes.Length / 512)).CopyTo($bytes, 0x1BE + 12)
$bytes[510] = 0x55; $bytes[511] = 0xAA
[IO.File]::WriteAllBytes($hybrid, $bytes)
Remove-Item $src -Recurse -Force -ErrorAction SilentlyContinue
Get-Item $win, $hybrid | Select-Object Name, Length
