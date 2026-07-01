<#
.SYNOPSIS
  Serves wwwroot/ over http://localhost and opens EdMd in Chrome.

.DESCRIPTION
  The editor UI needs a *secure context* for the File System Access API (real local
  open/save in the browser). http://localhost counts as secure, so this hosts the
  static files on a loopback TCP port and launches Chrome pointed at it.

  Uses a raw TcpListener on 127.0.0.1 (no admin / URL-ACL reservation needed) and
  serves GET requests for static assets only. Ctrl+C to stop.

.PARAMETER Port
  Loopback port to listen on (default 8080).

.PARAMETER NoLaunch
  Serve only; don't open a browser.
#>
[CmdletBinding()]
param(
  [int]$Port = 8080,
  [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$root = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "wwwroot"
if (-not (Test-Path $root)) { throw "wwwroot not found next to serve.ps1: $root" }

$mime = @{
  ".html"="text/html; charset=utf-8"; ".htm"="text/html; charset=utf-8";
  ".js"="text/javascript; charset=utf-8"; ".mjs"="text/javascript; charset=utf-8";
  ".css"="text/css; charset=utf-8"; ".json"="application/json; charset=utf-8";
  ".map"="application/json; charset=utf-8"; ".svg"="image/svg+xml";
  ".png"="image/png"; ".jpg"="image/jpeg"; ".jpeg"="image/jpeg"; ".gif"="image/gif";
  ".ico"="image/x-icon"; ".woff"="font/woff"; ".woff2"="font/woff2"; ".ttf"="font/ttf";
  ".md"="text/markdown; charset=utf-8"; ".txt"="text/plain; charset=utf-8";
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
$url = "http://localhost:$Port/index.html"
Write-Host "EdMd serving $root" -ForegroundColor Cyan
Write-Host "  $url   (Ctrl+C to stop)" -ForegroundColor Green

if (-not $NoLaunch) {
  # Open/save needs a Chromium browser (File System Access API). Prefer Chrome, then
  # Edge (always on Win10/11), then other Chromium browsers; fall back to the default.
  $browser = @(
    "${env:ProgramFiles}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "${env:LocalAppData}\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles}\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles}\BraveSoftware\Brave-Browser\Application\brave.exe",
    "${env:LocalAppData}\Programs\Opera\opera.exe",
    "${env:LocalAppData}\Vivaldi\Application\vivaldi.exe"
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1
  if ($browser) { Start-Process $browser $url } else { Start-Process $url }
}

function Send-Response($stream, [int]$code, [string]$status, [string]$contentType, [byte[]]$body) {
  $head = "HTTP/1.1 $code $status`r`n" +
          "Content-Type: $contentType`r`n" +
          "Content-Length: $($body.Length)`r`n" +
          "Cache-Control: no-store`r`n" +
          "Connection: close`r`n`r`n"
  $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)
  $stream.Write($headBytes, 0, $headBytes.Length)
  if ($body.Length) { $stream.Write($body, 0, $body.Length) }
}

try {
  while ($true) {
    $client = $listener.AcceptTcpClient()
    try {
      $stream = $client.GetStream()
      $reader = [System.IO.StreamReader]::new($stream)
      $requestLine = $reader.ReadLine()
      if (-not $requestLine) { continue }
      $parts = $requestLine -split ' '
      $method = $parts[0]; $target = $parts[1]

      if ($method -ne 'GET') {
        Send-Response $stream 405 "Method Not Allowed" "text/plain" ([Text.Encoding]::ASCII.GetBytes("405"))
        continue
      }

      # Strip query, decode, default to index.html, block traversal.
      $path = ($target -split '\?')[0]
      $path = [System.Uri]::UnescapeDataString($path)
      if ($path -eq '/' -or [string]::IsNullOrEmpty($path)) { $path = '/index.html' }
      $rel = $path.TrimStart('/').Replace('/', '\')
      $full = [System.IO.Path]::GetFullPath((Join-Path $root $rel))

      if (-not $full.StartsWith([System.IO.Path]::GetFullPath($root), [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path $full -PathType Leaf)) {
        Send-Response $stream 404 "Not Found" "text/plain; charset=utf-8" ([Text.Encoding]::UTF8.GetBytes("404 Not Found: $path"))
        continue
      }

      $ext = [System.IO.Path]::GetExtension($full).ToLowerInvariant()
      $ct = if ($mime.ContainsKey($ext)) { $mime[$ext] } else { "application/octet-stream" }
      $bytes = [System.IO.File]::ReadAllBytes($full)
      Send-Response $stream 200 "OK" $ct $bytes
    }
    catch { }
    finally { $client.Close() }
  }
}
finally { $listener.Stop() }
