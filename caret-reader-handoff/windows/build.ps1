<#
  光标朗读 gbTTS —— Windows 一键构建（P0 新增，GitHub Actions 也用它）
  做的事：编译 → 自动测试 → （可选）下载一个轻量音色 → 组装便携版文件夹 → --selftest 自检 → 打 zip

  用法（在 caret-reader-handoff\windows 文件夹里，PowerShell 7）：
    .\build.ps1                       # 编译 + 测试 + 便携 zip（不含音色、不跑自检）
    .\build.ps1 -WithVoice            # 另外下载“清朗 chaowen”音色，放进便携版，并运行 --selftest
    .\build.ps1 -WithVoice -Models D:\tts   # 模型源目录（已下载过就不再下载）

  需要：.NET 8 SDK；-WithVoice 还需要 Python 3（会 pip 安装 pypinyin，MIT 许可）。
  路径保持纯英文（sherpa-onnx 1.x 按 ANSI 传路径，见 DESIGN.md）。
#>
param(
    [switch]$WithVoice,
    [string]$Models = "",
    [string]$Out = "",
    [string]$Ver = "v0.6"
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$Here = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$env:PYTHONUTF8 = '1'; $env:PYTHONIOENCODING = 'utf-8'   # Windows 控制台默认 cp1252，Python 打印中文会报错
if (-not $Out) { $Out = Join-Path (Split-Path $Here -Parent) 'release' }
if (-not $Models) { $Models = Join-Path $Here 'models' }

function Step($s) { Write-Host "== $s" -ForegroundColor Cyan }

Step '1. 编译 Windows x64 程序 + 测试工具'
$Pub = Join-Path $Here 'publish\win-x64'
if (Test-Path $Pub) { Remove-Item $Pub -Recurse -Force }
dotnet publish "$Here\src\CaretReader.App" -c Release -r win-x64 -o $Pub -nologo -v q
dotnet build "$Here\tests\CaretReader.TestHarness" -c Release -nologo -v q

Step '2. 自动测试（xUnit）'
dotnet test "$Here\tests\CaretReader.Tests" -c Release -nologo -v q --logger "trx;LogFileName=tests.trx" --results-directory "$Here\TestResults"

Step '3. 命令行测试工具（切句 / 路径 / 句柄判空 / 音色包 / 取文字规则）'
$Harness = Join-Path $Here 'tests\CaretReader.TestHarness\bin\Release\net8.0\CaretReader.TestHarness.exe'
foreach ($t in 'split', 'paths', 'nullguard', 'packtest', 'v06') {
    & $Harness $t | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "测试 $t 失败（退出码 $LASTEXITCODE）" }
}

$Voices = @()
if ($WithVoice) {
    Step '4. 准备轻量音色：清朗 chaowen-0（Piper，sherpa-onnx 官方模型）'
    $dl = Join-Path $Models 'dl'
    $cw = Join-Path $dl 'vits-piper-zh_CN-chaowen-medium'
    if (-not (Test-Path (Join-Path $cw 'zh_CN-chaowen-medium.onnx'))) {
        New-Item -ItemType Directory -Force $dl | Out-Null
        $tar = Join-Path $dl 'chaowen.tar.bz2'
        Invoke-WebRequest 'https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-zh_CN-chaowen-medium.tar.bz2' -OutFile $tar
        tar -xjf $tar -C $dl
        Remove-Item $tar
    }
    python -m pip install --quiet "pypinyin==0.53.0"
    $env:GBTTS_MODELS = $Models
    python "$Here\packs.py" assemble "$Here\pk\voices" chaowen-0
    Copy-Item "$Here\config\samples\chaowen-0.mp3" "$Here\pk\voices\chaowen-0\sample.mp3"
    $Voices = @('chaowen-0')
    Step '4b. 第 2 轮检查：转换脚本 + 中文路径加载、去首尾静音、字/分统一（清朗 + 转换出的音色）'
    # 转换脚本自动检查：把官方 chaowen onnx 去掉 sherpa 元数据（= 朋友刚从 Piper 导出的样子），
    # 用 tools/onnx2voicepack.py 转到中文路径下，再和清朗一起跑节奏检查（sherpa 能加载 + 跨音色字/分误差）
    python -m pip install --quiet "onnx==1.17.0" "numpy<3"
    $Friend = Join-Path $env:TEMP '朋友的模型'
    $Conv = Join-Path $env:TEMP '转换输出 测试\voices'
    foreach ($d in $Friend, $Conv) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    New-Item -ItemType Directory -Force $Friend | Out-Null
    python -c "import onnx,sys; m=onnx.load(sys.argv[1]); del m.metadata_props[:]; onnx.save(m, sys.argv[2])" "$cw\zh_CN-chaowen-medium.onnx" "$Friend\friend-voice.onnx"
    Copy-Item "$cw\zh_CN-chaowen-medium.onnx.json" "$Friend\friend-voice.onnx.json"
    python "$Here\tools\onnx2voicepack.py" $Friend $Conv --name '转换测试'
    if ($LASTEXITCODE -ne 0) { throw "onnx2voicepack.py 转换失败（退出码 $LASTEXITCODE）" }
    New-Item -ItemType Directory -Force "$Friend\bad" | Out-Null
    Set-Content "$Friend\bad\x.ckpt" 'x'
    $PSNativeCommandUseErrorActionPreference = $false   # 这一步预期退出码 2
    python "$Here\tools\onnx2voicepack.py" "$Friend\bad" $Conv | Out-Host
    $code = $LASTEXITCODE
    $PSNativeCommandUseErrorActionPreference = $true
    if ($code -ne 2) { throw "onnx2voicepack.py 对不认识的格式应退出码 2（实际 $code）" }
    & $Harness pace "$Here\pk\voices\chaowen-0" "$Conv\friend-voice" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "第 2 轮节奏检查失败（退出码 $LASTEXITCODE）" }
}

Step '5. 组装便携版文件夹'
$Stage = Join-Path $Here 'stage'
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
$App = Join-Path $Stage '光标朗读'
New-Item -ItemType Directory -Force "$App\voices", "$App\frontend", "$App\licenses" | Out-Null
Copy-Item "$Pub\*" $App -Recurse
Copy-Item "$Here\frontend\data\*.txt" "$App\frontend\"
Copy-Item "$Here\frontend\licenses\*.txt" "$App\licenses\"
Copy-Item "$Here\frontend\用户自定义词库.txt" "$App\用户自定义词库.txt"
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[IO.File]::WriteAllText("$App\说明.txt", [IO.File]::ReadAllText("$Here\README_zh.md"), $utf8Bom)
[IO.File]::WriteAllText("$App\THIRD_PARTY_NOTICES.txt", [IO.File]::ReadAllText("$Here\frontend\THIRD_PARTY_NOTICES.txt"), $utf8Bom)
[IO.File]::WriteAllText("$App\edition.txt", $(if ($Voices) { '轻量版' } else { '无音色版' }))
foreach ($v in $Voices) { Copy-Item "$Here\pk\voices\$v" "$App\voices\$v" -Recurse }

if ($WithVoice) {
    Step '6. 自检：CaretReader.exe --selftest（加载音色并合成一句话）'
    $p = Start-Process -FilePath "$App\CaretReader.exe" -ArgumentList '--selftest' -WorkingDirectory $App -PassThru
    if (-not $p.WaitForExit(300000)) { $p.Kill(); throw '自检超过 5 分钟没有结束' }
    $report = Join-Path $App 'selftest.txt'
    if (Test-Path $report) { Get-Content $report -Encoding utf8 | Out-Host } else { Write-Host '没有生成 selftest.txt' }
    if ($p.ExitCode -ne 0) { throw "自检失败（退出码 $($p.ExitCode)）" }
    # 自检产生的文件不进发布包（日志 / wav / 设置）
    Get-ChildItem $App -File | Where-Object { $_.Name -like 'selftest*' -or $_.Name -in 'log.txt', 'settings.json', 'crash_stage.txt' } |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $Here 'TestResults') -ErrorAction SilentlyContinue; Remove-Item $_.FullName }
    # 第 2 轮：自检若产生了 data\（节奏设置 / 测速缓存）也不进发布包
    if (Test-Path "$App\data") { Remove-Item "$App\data" -Recurse -Force }
}

Step '7. 打 zip'
New-Item -ItemType Directory -Force "$Out\$Ver" | Out-Null
$ed = $(if ($Voices) { 'lite' } else { 'novoice' })
$zip = Join-Path "$Out\$Ver" "gbTTS_portable_win10x64_${Ver}_$ed.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $App -DestinationPath $zip -CompressionLevel Optimal
'{0}  {1:N1} MB' -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB) | Out-Host
Step '完成'
