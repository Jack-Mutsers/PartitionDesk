# Runs the engine self-test against a temporary 1 GB virtual disk. Must run elevated.
# Only the VHDX created here is touched; the test refuses to run on any other disk.
param([string]$Only = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$vhd = Join-Path $here 'selftest.vhdx'
$log = Join-Path $here 'selftest.log'
Start-Transcript -Path $log -Force | Out-Null
try {
    dotnet build (Join-Path $here 'SelfTest.csproj') -c Debug -o (Join-Path $here 'bin\out') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    if (Test-Path $vhd) { Dismount-DiskImage -ImagePath $vhd -ErrorAction SilentlyContinue | Out-Null; Remove-Item $vhd -Force }
    "create vdisk file=`"$vhd`" maximum=1024 type=expandable`nattach vdisk" | Set-Content "$env:TEMP\pd-selftest.txt" -Encoding ascii
    diskpart /s "$env:TEMP\pd-selftest.txt" | Out-Null
    Start-Sleep 2
    $num = (Get-DiskImage -ImagePath $vhd | Get-Disk).Number
    Write-Host "Virtual disk attached as disk $num"

    & (Join-Path $here 'bin\out\SelfTest.exe') $num $vhd $Only
    $code = $LASTEXITCODE
}
finally {
    if (Test-Path $vhd) { Dismount-DiskImage -ImagePath $vhd -ErrorAction SilentlyContinue | Out-Null; Remove-Item $vhd -Force -ErrorAction SilentlyContinue }
    Stop-Transcript | Out-Null
}
exit $code
