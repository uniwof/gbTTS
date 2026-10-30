// v0.4：检查试听 mp3 能被 NLayer（程序里用的解码器）正确解码；可选第二参数：把第一个文件解码成 raw float32 方便和 ffmpeg 对比
foreach (var f in Directory.GetFiles(args[0], "*.mp3").OrderBy(x => x))
{
    using var m = new NLayer.MpegFile(f);
    var buf = new float[8192]; long n = 0; int k; float peak = 0;
    using var raw = args.Length > 1 && f.EndsWith(args[1]) ? File.Create("/tmp/nl.f32") : null;
    while ((k = m.ReadSamples(buf, 0, buf.Length)) > 0) { n += k; for (int i = 0; i < k; i++) { peak = Math.Max(peak, Math.Abs(buf[i])); raw?.Write(BitConverter.GetBytes(buf[i])); } }
    Console.WriteLine($"{Path.GetFileName(f)}\t{m.SampleRate}Hz\tch={m.Channels}\t{n / (double)m.SampleRate / m.Channels:0.00}s\tpeak={peak:0.00}");
}
