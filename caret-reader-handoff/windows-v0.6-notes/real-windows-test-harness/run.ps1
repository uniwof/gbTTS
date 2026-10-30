# 用法：run.ps1 -App C:\CRT\v05 -Tag v05 -Targets notepad,wordpad -Mode bar|ball
param([string]$App='C:\CRT\v05', [string]$Tag='v05', [string[]]$Targets=@('notepad'), [string]$Mode='bar', [switch]$NoStart, [switch]$Shots)
$ErrorActionPreference='Continue'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
if (-not ('W' -as [type])) { Add-Type -TypeDefinition ([IO.File]::ReadAllText('C:\CRT\w.cs')) }
[void][W]::SetProcessDPIAware()
$log = Join-Path $App 'log.txt'
$res = "C:\CRT\results_$Tag.txt"
function Out-R($s) { Add-Content -LiteralPath $res -Value $s -Encoding UTF8; $s }
function Shot($name) {
  if (-not $Shots) { return }
  Add-Type -AssemblyName System.Drawing
  $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
  $g=[Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0,0,0,0,$bmp.Size); $g.Dispose()
  $sc = [Math]::Min(1.0, 1400 / $b.Width); $sm = New-Object Drawing.Bitmap ([int]($b.Width*$sc)), ([int]($b.Height*$sc))
  $g2=[Drawing.Graphics]::FromImage($sm); $g2.InterpolationMode='HighQualityBicubic'; $g2.DrawImage($bmp,0,0,$sm.Width,$sm.Height); $g2.Dispose()
  New-Item -ItemType Directory -Force C:\CRT\shots | Out-Null
  $sm.Save("C:\CRT\shots\$name.png", [Drawing.Imaging.ImageFormat]::Png)
}
Add-Type -AssemblyName System.Windows.Forms

function LogCount { if (Test-Path $log) { (Get-Content -LiteralPath $log -Encoding UTF8).Count } else { 0 } }
function NewLog($n) { if (Test-Path $log) { Get-Content -LiteralPath $log -Encoding UTF8 | Select-Object -Skip $n } }

function AppWindow {
  $p = Get-Process CaretReader -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $p) { return [IntPtr]::Zero }
  # 主窗口：最大的那个可见窗口（悬浮球 / 横条）
  $best=[IntPtr]::Zero; $area=0
  foreach ($h in [W]::All('CaretReader')) { $r=New-Object W+RECT; [void][W]::GetWindowRect($h,[ref]$r); $w=$r.R-$r.L; $hh=$r.B-$r.T; $a=$w*$hh
    if ($Mode -eq 'ball') { if ([Math]::Abs($w-$hh) -le 2 -and $w -lt 260 -and $w -gt 30) { return $h } ; continue }   # 悬浮球：正方形的那个窗口（不是提示气泡）
    if ($a -gt $area -and $w -lt 900) { $area=$a; $best=$h } }
  return $best
}
function ClickPoint {
  $h = AppWindow; $r=New-Object W+RECT; [void][W]::GetWindowRect($h,[ref]$r)
  $dpi = [W]::GetDpiForWindow($h); if ($dpi -eq 0) { $dpi = 96 }; $s = $dpi/96.0
  if ($Mode -eq 'bar') { return @([int]($r.L + 276*$s), [int]($r.T + 32*$s)) }   # v0.5 横条上的「开始」按钮
  return @([int](($r.L+$r.R)/2), [int](($r.T+$r.B)/2))
}
function BlankPoint {   # v0.5 横条上状态文字那一行（空白处）
  $h = AppWindow; $r=New-Object W+RECT; [void][W]::GetWindowRect($h,[ref]$r)
  $dpi = [W]::GetDpiForWindow($h); if ($dpi -eq 0) { $dpi = 96 }; $s = $dpi/96.0
  return @([int]($r.L + 120*$s), [int]($r.T + 64*$s))
}

if (-not $NoStart) {
  Get-Process CaretReader -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
  if (Test-Path $log) { Remove-Item $log -Force }
  $env:CARETREADER_TESTLOG = '1'
  Start-Process -FilePath (Join-Path $App 'CaretReader.exe') -WorkingDirectory $App
  $t0 = Get-Date
  while (((Get-Date)-$t0).TotalSeconds -lt 90) { Start-Sleep 1; if ((Test-Path $log) -and (Select-String -LiteralPath $log -Pattern '模型已加载' -Quiet)) { break } }
  Start-Sleep 2
  Out-R "[$Tag] 启动用时 $([int]((Get-Date)-$t0).TotalSeconds)s；模型已加载=$((Select-String -LiteralPath $log -Pattern '模型已加载' -Quiet))"
  Shot "${Tag}_started"
}

function OpenTarget($t) {
  switch ($t) {
    'notepad' { Start-Process notepad 'C:\CRT\t.txt'; Start-Sleep 2; return [W]::Find('notepad', $null) }
    'wordpad' { Start-Process 'C:\Program Files\Windows NT\Accessories\wordpad.exe' 'C:\CRT\t.rtf'; Start-Sleep 3; return [W]::Find('wordpad', $null) }
    'edgehtml' { Start-Process msedge @('--new-window','--no-first-run','--no-default-browser-check','--user-data-dir=C:\CRT\edgeprof','file:///C:/CRT/t.html'); Start-Sleep 6; return [W]::Find('msedge', 'Chrome_WidgetWin_1') }
    'edgepdf' { Start-Process msedge @('--new-window','--no-first-run','--no-default-browser-check','--user-data-dir=C:\CRT\edgeprof','file:///C:/CRT/t.pdf'); Start-Sleep 7; return [W]::Find('msedge', 'Chrome_WidgetWin_1') }
    'chromehtml' { $c=(Get-Command chrome -EA 0).Source; if(-not $c){$c='C:\Program Files\Google\Chrome\Application\chrome.exe'}; if(-not (Test-Path $c)){return [IntPtr]::Zero}; Start-Process $c @('--new-window','--no-first-run','--no-default-browser-check','--user-data-dir=C:\CRT\chprof','file:///C:/CRT/t.html'); Start-Sleep 6; return [W]::Find('chrome', 'Chrome_WidgetWin_1') }
    'sumatra' { Start-Process 'C:\CRT\sumatra\SumatraPDF.exe' @('-new-window','C:\CRT\t.pdf'); Start-Sleep 4; return [W]::Find('SumatraPDF', $null) }
  }
}
function CloseTarget($t) {
  switch ($t) { 'notepad' { Get-Process notepad -EA 0 | Stop-Process -Force } 'wordpad' { Get-Process wordpad -EA 0 | Stop-Process -Force }
    'edgehtml' { Get-Process msedge -EA 0 | Stop-Process -Force } 'edgepdf' { Get-Process msedge -EA 0 | Stop-Process -Force } 'sumatra' { Get-Process SumatraPDF -EA 0 | Stop-Process -Force } 'chromehtml' { Get-Process chrome -EA 0 | Stop-Process -Force } }
  Start-Sleep 1
}
function FocusDoc($h, $t) {
  $r=New-Object W+RECT; [void][W]::GetWindowRect($h,[ref]$r)
  # 点击文档正文区域（真人操作），浏览器 / PDF 点在页面上方正文处
  $x = [int]($r.L + ($r.R-$r.L)*0.35); $y = [int]($r.T + ($r.B-$r.T)*0.30)
  if ($t -eq 'sumatra' -or $t -eq 'edgepdf') { $y = [int]($r.T + ($r.B-$r.T)*0.25) }
  [W]::Click($x,$y); Start-Sleep -Milliseconds 400
  if ([W]::GetForegroundWindow() -ne $h) { [void][W]::Activate($h) }
}
function PlaceCaret($t) { if ($t -in 'notepad','wordpad') { [W]::Chord(0x11,0x24); Start-Sleep -Milliseconds 150; [W]::Repeat(0x27,5,$false) } }
function Select-Text($t) {
  if ($t -in 'notepad','wordpad') { [W]::Chord(0x11,0x24); Start-Sleep -Milliseconds 150; [W]::Repeat(0x27,8,$true) }
  else { [W]::Chord(0x11,0x41) }   # 浏览器 / PDF：Ctrl+A 全选
  Start-Sleep -Milliseconds 300
}
function StopRead { [W]::HumanChord(120, 0x11,0x12,0x53); Start-Sleep -Milliseconds 1500 }

function Case($t, $h, $name, [scriptblock]$prep, [scriptblock]$trigger) {
  FocusDoc $h $t
  & $prep
  $fg0 = [W]::Fg()
  $n = LogCount
  & $trigger
  Start-Sleep -Milliseconds 700
  $fg1 = [W]::Fg()
  Start-Sleep -Milliseconds 2300
  Shot "${Tag}_${t}_${name}"
  $lines = NewLog $n | Where-Object { $_ -match '取文字|剪贴板|UIA|TEST|窗口|前台|权限|朗读结束|焦点|快捷键|点击|单击|双击' }
  StopRead
  Out-R "---- [$Tag] $t / $name"
  Out-R "  触发前前台: $fg0"
  Out-R "  触发后0.7s前台: $fg1"
  foreach ($l in $lines) { Out-R ("  log: " + ($l -replace '^\S+\s+\S+\s+','')) }
}

foreach ($t in $Targets) {
  $h = OpenTarget $t
  if ($h -eq [IntPtr]::Zero) { Out-R "---- [$Tag] $t：打不开目标程序，跳过"; continue }
  Start-Sleep 1
  Case $t $h '光标后点击' { PlaceCaret $t } { $p = ClickPoint; [W]::Click($p[0],$p[1]) }
  Case $t $h '选中后点击' { Select-Text $t } { $p = ClickPoint; [W]::Click($p[0],$p[1]) }
  Case $t $h '后台快捷键_光标' { PlaceCaret $t } { [W]::HumanChord(250, 0x11,0x12,0x52) }
  Case $t $h '后台快捷键_选中' { Select-Text $t } { [W]::HumanChord(250, 0x11,0x12,0x52) }
  if ($Mode -eq 'bar') {
    # 用户描述 (c)：先点过一次软件，再回文档按快捷键 / 不回文档直接按快捷键
    Case $t $h '点软件空白后快捷键' { PlaceCaret $t; $b = BlankPoint; [W]::Click($b[0],$b[1]); Start-Sleep -Milliseconds 500 } { [W]::HumanChord(250, 0x11,0x12,0x52) }
  } else {
    Case $t $h '右键菜单后快捷键' { PlaceCaret $t; $p = ClickPoint; [W]::RClick($p[0],$p[1]); Start-Sleep -Milliseconds 800; [W]::Key(0x1B,$false); [W]::Key(0x1B,$true); Start-Sleep -Milliseconds 500 } { [W]::HumanChord(250, 0x11,0x12,0x52) }
  }
  CloseTarget $t
}
"done"
