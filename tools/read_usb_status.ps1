<#
Read-only diagnostic capture for a live/frozen ZeroSense board.
Close ZeroSense first. Does not configure, arm, reset or flash the device.
Example: .\tools\read_usb_status.ps1 -Port COM4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^COM[1-9][0-9]*$')]
    [string]$Port,
    [ValidateRange(1, 10)]
    [int]$Samples = 3
)

$ErrorActionPreference = 'Stop'
if (Get-Process zerosense -ErrorAction SilentlyContinue) {
    throw 'Close ZeroSense before capturing status; the serial port must have only one reader.'
}

$diagnosticPort = [System.IO.Ports.SerialPort]::new($Port, 115200)
$diagnosticPort.DtrEnable = $true
$diagnosticPort.ReadTimeout = 200
$diagnosticPort.WriteTimeout = 500
$receivedStatus = $false
try {
    $diagnosticPort.Open()
    for ($sample = 1; $sample -le $Samples; $sample++) {
        # Version 1, little-endian sequence, STATUS (0xFC), empty payload.
        [byte[]]$body = @(1, $sample, 0, 252, 0)
        [int]$crc = 65535
        foreach ($value in $body) {
            $crc = $crc -bxor ([int]$value -shl 8)
            for ($bit = 0; $bit -lt 8; $bit++) {
                $crc = if ($crc -band 32768) {
                    (($crc -shl 1) -bxor 4129) -band 65535
                } else { ($crc -shl 1) -band 65535 }
            }
        }
        [byte[]]$frame = @(165, 90) + $body + @(($crc -band 255), ($crc -shr 8))
        "Sample $sample UTC $([DateTime]::UtcNow.ToString('O'))"
        $diagnosticPort.Write($frame, 0, $frame.Length)
        $deadline = [DateTime]::UtcNow.AddSeconds(1)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $line = $diagnosticPort.ReadLine().Trim()
                if ($line.StartsWith('STATUS:HASH=')) { $receivedStatus = $true }
                if ($line) { $line }
            } catch [System.TimeoutException] { }
        }
    }
    if (-not $receivedStatus) {
        throw "No ZeroSense STATUS reply on $Port. This capture cannot confirm a healthy board."
    }
} finally {
    $diagnosticPort.Dispose()
}
