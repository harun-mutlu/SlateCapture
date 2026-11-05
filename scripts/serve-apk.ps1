param([int]$Minutes = 2, [int]$Port = 47902)
. "$PSScriptRoot\common.ps1"

$apk = Get-ChildItem $Dist -Filter 'SlateCapture-*.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw 'No APK found. Run build.cmd first.' }

$ip = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
    Select-Object -First 1 -ExpandProperty IPv4Address | Select-Object -First 1 -ExpandProperty IPAddress
if (-not $ip) { throw 'No active network connection found.' }

$bytes = [IO.File]::ReadAllBytes($apk.FullName)
$page = [Text.Encoding]::UTF8.GetBytes(@"
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>SlateCapture</title></head>
<body style="margin:0;background:#0F1217;color:#E9ECF1;font-family:sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;text-align:center">
<div><h1 style="font-size:28px">SlateCapture</h1><p style="color:#A3AAB6">$($apk.Name)</p>
<a href="/SlateCapture.apk" style="display:inline-block;margin-top:16px;padding:16px 36px;border-radius:28px;background:#4C8DFF;color:#fff;text-decoration:none;font-size:18px">Download</a></div>
</body></html>
"@)

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, $Port)
$listener.Start()
$until = (Get-Date).AddMinutes($Minutes)

Write-Host ''
Write-Host "  Open this address in the tablet's browser:" -ForegroundColor Cyan
Write-Host ''
Write-Host "      http://${ip}:$Port" -ForegroundColor Green
Write-Host ''
Write-Host "  The page closes at $($until.ToString('HH:mm:ss'))."

try {
    while ((Get-Date) -lt $until) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 200; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream)
            $request = $reader.ReadLine()
            while ($reader.ReadLine()) { }
            if ($request -match '^GET /SlateCapture\.apk') {
                $body = $bytes
                $head = "HTTP/1.1 200 OK`r`nContent-Type: application/vnd.android.package-archive`r`nContent-Disposition: attachment; filename=`"$($apk.Name)`"`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
                Write-Host "  APK sent to $($client.Client.RemoteEndPoint)"
            } else {
                $body = $page
                $head = "HTTP/1.1 200 OK`r`nContent-Type: text/html; charset=utf-8`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
            }
            $h = [Text.Encoding]::ASCII.GetBytes($head)
            $stream.Write($h, 0, $h.Length)
            $stream.Write($body, 0, $body.Length)
            $stream.Flush()
        } catch {
        } finally {
            $client.Close()
        }
    }
} finally {
    $listener.Stop()
}
Write-Host "`n  Closed." -ForegroundColor Green
