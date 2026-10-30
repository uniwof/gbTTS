$ErrorActionPreference='Continue'
"== 声卡检查"
Set-Service Audiosrv -StartupType Automatic -EA 0; Start-Service Audiosrv -EA 0; Set-Service AudioEndpointBuilder -StartupType Automatic -EA 0; Start-Service AudioEndpointBuilder -EA 0
$d = Get-CimInstance Win32_SoundDevice -EA 0; "声卡设备: " + (($d | ForEach-Object Name) -join ', ')
if (-not $d) {
  try {
    (New-Object Net.WebClient).DownloadFile('https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip','C:\CRT\vb.zip')
    Expand-Archive C:\CRT\vb.zip C:\CRT\vb -Force
    $cat = Get-ChildItem C:\CRT\vb -Filter *.cat | Select-Object -First 1
    $sig = Get-AuthenticodeSignature $cat.FullName
    if ($sig.SignerCertificate) { $st = New-Object Security.Cryptography.X509Certificates.X509Store 'TrustedPublisher','LocalMachine'; $st.Open('ReadWrite'); $st.Add($sig.SignerCertificate); $st.Close(); "已信任驱动发布者: " + $sig.SignerCertificate.Subject }
    $p = Start-Process 'C:\CRT\vb\VBCABLE_Setup_x64.exe' -ArgumentList '-i','-h' -PassThru
    if (-not $p.WaitForExit(150000)) { $p.Kill(); "VB-Cable 安装超时" }
    Start-Sleep 5; Restart-Service Audiosrv -Force -EA 0
  } catch { "虚拟声卡安装失败: $_" }
  $d = Get-CimInstance Win32_SoundDevice -EA 0; "安装后声卡设备: " + (($d | ForEach-Object Name) -join ', ')
}
Add-Type -AssemblyName System.Windows.Forms
try { Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class WO { [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs(); }' } catch {}
"waveOut 设备数: " + [WO]::waveOutGetNumDevs()
