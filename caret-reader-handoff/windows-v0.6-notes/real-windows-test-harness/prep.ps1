$ErrorActionPreference='Continue'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
if (-not (Test-Path C:\CRT\v06)) { Copy-Item C:\CRT\v05 C:\CRT\v06 -Recurse; Copy-Item C:\CRT\v06x\CaretReader.exe C:\CRT\v06\ -Force }
$edge = (Get-Command msedge -EA 0).Source; if (-not $edge) { $edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' }
"edge: $edge exists=" + (Test-Path $edge)
"chrome exists=" + (Test-Path 'C:\Program Files\Google\Chrome\Application\chrome.exe')
if (-not (Test-Path C:\CRT\t.pdf) -and (Test-Path $edge)) { & $edge --headless --disable-gpu --no-first-run "--user-data-dir=C:\CRT\edgehl" "--print-to-pdf=C:\CRT\t.pdf" "file:///C:/CRT/t.html" | Out-Null; Start-Sleep 6 }
if (-not (Test-Path C:\CRT\t.pdf)) { $c='C:\Program Files\Google\Chrome\Application\chrome.exe'; if (Test-Path $c) { & $c --headless --disable-gpu "--print-to-pdf=C:\CRT\t.pdf" "file:///C:/CRT/t.html" | Out-Null; Start-Sleep 6 } }
"pdf: " + (Test-Path C:\CRT\t.pdf)
try { (New-Object Net.WebClient).DownloadFile('https://www.sumatrapdfreader.org/dl/rel/3.5.2/SumatraPDF-3.5.2-64.zip','C:\CRT\s.zip'); Expand-Archive C:\CRT\s.zip C:\CRT\sumatra -Force; Get-ChildItem C:\CRT\sumatra *.exe | Rename-Item -NewName SumatraPDF.exe -EA 0 } catch { "sumatra 下载失败: $_" }
"sumatra: " + (Test-Path C:\CRT\sumatra\SumatraPDF.exe)
"wordpad: " + (Test-Path 'C:\Program Files\Windows NT\Accessories\wordpad.exe')
"winver: " + [Environment]::OSVersion.VersionString + " / " + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').ProductName
"elevated: " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Type -AssemblyName System.Windows.Forms
"screen: " + [Windows.Forms.Screen]::PrimaryScreen.Bounds
"cpu: " + (Get-CimInstance Win32_Processor | Select-Object -First 1).Name + " cores " + [Environment]::ProcessorCount + " ram " + [int]((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1MB) + "MB"
"session: " + [Environment]::UserInteractive + " user " + $env:USERNAME
