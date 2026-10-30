# 资源实测：分别以「小猫」和「简单圆球」外观启动 v0.6，测空闲 60 秒和朗读 40 秒的 CPU / 内存
param([string]$App='C:\CRT\v06')
$ErrorActionPreference='Continue'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
if (-not ('W' -as [type])) { Add-Type -TypeDefinition ([IO.File]::ReadAllText('C:\CRT\w.cs')) }
$res='C:\CRT\results_resource.txt'
function Out-R($s){ Add-Content -LiteralPath $res -Value $s -Encoding UTF8; $s }
$cores=[Environment]::ProcessorCount
function Sample($p,$sec){
  $p.Refresh(); $c0=$p.TotalProcessorTime.TotalMilliseconds; $t0=Get-Date; $ws=@(); $pv=@(); $gdi=0
  for($i=0;$i -lt $sec;$i++){ Start-Sleep 1; $p.Refresh(); $ws+=$p.WorkingSet64; $pv+=$p.PrivateMemorySize64 }
  $p.Refresh(); $c1=$p.TotalProcessorTime.TotalMilliseconds; $el=((Get-Date)-$t0).TotalMilliseconds
  $cpu=($c1-$c0)/$el*100
  $wsA=($ws|Measure-Object -Average -Maximum); $pvA=($pv|Measure-Object -Average -Maximum)
  return ('CPU 平均 {0:N2}% 单核（整机 {1:N2}% / {2} 核），CPU 时间 {3:N0} ms；工作集 平均 {4:N1} MB 峰值 {5:N1} MB；私有内存 平均 {6:N1} MB 峰值 {7:N1} MB；句柄 {8} GDI对象 {9}' -f $cpu, ($cpu/$cores), $cores, ($c1-$c0), ($wsA.Average/1MB), ($wsA.Maximum/1MB), ($pvA.Average/1MB), ($pvA.Maximum/1MB), $p.HandleCount, [W]::Gdi($p.Handle))
}
foreach($look in 'pet','ball'){
  Get-Process CaretReader -EA 0 | Stop-Process -Force; Start-Sleep 2
  $log=Join-Path $App 'log.txt'; if(Test-Path $log){Remove-Item $log -Force}
  Set-Content -LiteralPath (Join-Path $App 'settings.json') -Value ('{"Appearance":"'+$look+'","TopMost":true}') -Encoding UTF8
  $p=Start-Process -FilePath (Join-Path $App 'CaretReader.exe') -WorkingDirectory $App -PassThru
  $t0=Get-Date; while(((Get-Date)-$t0).TotalSeconds -lt 90){ Start-Sleep 1; if((Test-Path $log) -and (Select-String -LiteralPath $log -Pattern '模型已加载' -Quiet)){break} }
  Start-Sleep 5
  Out-R ("[$look] 空闲 60 秒: " + (Sample $p 60))
  Start-Process notepad 'C:\CRT\long.txt'; Start-Sleep 2; $h=[W]::Find('notepad',$null); [void][W]::Activate($h)
  [W]::Chord(0x11,0x24); Start-Sleep -Milliseconds 300
  [W]::HumanChord(150,0x11,0x12,0x52); Start-Sleep 3
  Out-R ("[$look] 朗读 40 秒: " + (Sample $p 40))
  [W]::HumanChord(120,0x11,0x12,0x53); Start-Sleep 2
  Get-Process notepad -EA 0 | Stop-Process -Force
  Out-R ("[$look] 朗读日志: " + ((Get-Content $log -Encoding UTF8 | Select-String '朗读|音频|声卡|出错' | Select-Object -Last 6) -join ' | '))
}
Get-Process CaretReader -EA 0 | Stop-Process -Force
Remove-Item (Join-Path $App 'settings.json') -EA 0
