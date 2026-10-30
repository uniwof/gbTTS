$ErrorActionPreference='Continue'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Force C:\CRT | Out-Null
Get-Process CaretReader -EA 0 | Stop-Process -Force
(New-Object Net.WebClient).DownloadFile($env:CRT_URL, 'C:\CRT\bundle.zip')
Expand-Archive -LiteralPath 'C:\CRT\bundle.zip' -DestinationPath 'C:\CRT' -Force
Remove-Item C:\CRT\bundle.zip
if (-not (Test-Path C:\CRT\t.pdf)) {
  $edge = (Get-Command msedge -EA 0).Source; if (-not $edge) { $edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' }
  & $edge --headless --disable-gpu --no-first-run "--user-data-dir=C:\CRT\edgehl" "--print-to-pdf=C:\CRT\t.pdf" "file:///C:/CRT/t.html" | Out-Null
  Start-Sleep 5
}
if (-not (Test-Path C:\CRT\sumatra\SumatraPDF.exe)) {
  try { (New-Object Net.WebClient).DownloadFile('https://www.sumatrapdfreader.org/dl/rel/3.5.2/SumatraPDF-3.5.2-64.zip','C:\CRT\s.zip'); Expand-Archive C:\CRT\s.zip C:\CRT\sumatra -Force; Get-ChildItem C:\CRT\sumatra *.exe | Rename-Item -NewName SumatraPDF.exe -EA 0 } catch { "sumatra 下载失败: $_" }
}
Get-ChildItem C:\CRT | Select-Object Name, Length | Format-Table -AutoSize | Out-String
"wordpad: " + (Test-Path 'C:\Program Files\Windows NT\Accessories\wordpad.exe')
"winver: " + [Environment]::OSVersion.VersionString + " / " + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion
"elevated: " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
"screen: " + (Add-Type -AssemblyName System.Windows.Forms -PassThru | Out-Null; [Windows.Forms.Screen]::PrimaryScreen.Bounds)
"cpu: " + (Get-CimInstance Win32_Processor | Select-Object -First 1).Name + " cores " + [Environment]::ProcessorCount + " ram " + [int]((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1MB) + "MB"
