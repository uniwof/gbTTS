using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Security.Cryptography;

namespace CaretReader.Core;

/// <summary>
/// v0.4：「+」导入音色包。
///
/// 用户可以选任意盘符、任意（包括中文）路径的文件夹：
///   * 选的就是音色包（里面有 voice.json）→ 导入这一个；
///   * 选的是外层文件夹（解压时多套了一层，或者一个文件夹里放了好几个包）→ 往下找两层，全部导入。
/// 做法：先校验 → 复制到 voices 下的临时文件夹（.import-xxxx）→ 再校验 → 改名成英文文件夹名。
/// 因为最终都在“程序文件夹\voices\英文名\”里，原生库拿到的永远是英文相对路径，中文源路径不受影响
/// （复制用的是 .NET 的 Unicode 文件 API，不经过 sherpa-onnx）。
///
/// 省空间：大文件（≥1MB）如果 voices 里已经有一模一样的（大小相同且 SHA-256 相同，例如几个 zh-ll 音色共用的模型），
/// 在 NTFS 上用“硬链接”代替复制——不额外占硬盘；删掉任何一个文件夹都不影响别的包。
/// 不支持硬链接（U 盘的 FAT32/exFAT 等）时自动改为普通复制。
/// </summary>
public static class VoiceImporter
{
    /// <summary>审查采纳：遍历文件时跳过“联接 / 符号链接”文件夹，避免链接成环导致无限复制。</summary>
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    public sealed record Item(string Source, string? Id, string? Name, string? Error, bool Skipped = false);

    public sealed record Result(List<Item> Items)
    {
        public IEnumerable<Item> Imported => Items.Where(i => i.Error == null && !i.Skipped);
    }

    /// <summary>在 source 里找音色包文件夹（自己，或往下两层）。</summary>
    public static List<string> FindPacks(string source)
    {
        var list = new List<string>();
        if (File.Exists(Path.Combine(source, VoicePack.ManifestName))) { list.Add(source); return list; }
        foreach (var d1 in SafeDirs(source))
        {
            if (File.Exists(Path.Combine(d1, VoicePack.ManifestName))) { list.Add(d1); continue; }
            foreach (var d2 in SafeDirs(d1))
                if (File.Exists(Path.Combine(d2, VoicePack.ManifestName))) list.Add(d2);
        }
        return list;
    }

    /// <summary>
    /// 导入。progress(说明文字, 0~1) 会在后台线程被调用。出错不抛异常，结果里逐项说明。
    /// </summary>
    public static Result Import(string source, string appDir, Action<string, double>? progress, CancellationToken token)
    {
        // A3：.gbvoice（= zip 改扩展名，内含 voice.json + 模型）→ 先安全解压到程序文件夹下的临时目录，再按文件夹导入
        if (File.Exists(source) && IsPackFile(source))
        {
            string tmp = Path.Combine(Path.GetFullPath(appDir), ".gbvoice-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                progress?.Invoke("正在解压音色包…", 0);
                string? err = SafeExtract(source, tmp, token);
                if (err != null) return new Result(new() { new Item(source, null, Path.GetFileNameWithoutExtension(source), err) });
                var r = Import(tmp, appDir, progress, token);
                // 来源显示成原文件
                return new Result(r.Items.Select(i => i with { Source = source }).ToList());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write("解压音色包失败：" + ex);
                string msg = ex is UnauthorizedAccessException ? "程序所在的文件夹不允许写入，请把程序放到 D:\\ 或“文档”等位置。" : ex.Message;
                return new Result(new() { new Item(source, null, Path.GetFileNameWithoutExtension(source), msg) });
            }
            finally { TryDelete(tmp); }
        }

        var items = new List<Item>();
        string voices = VoiceLibrary.VoicesDir(appDir);
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (IsUnder(source, voices) || IsUnder(voices, source))
        {
            items.Add(new Item(source, null, null, "这个文件夹就在（或包含）程序自己的「音色」文件夹，不需要导入"));
            return new Result(items);
        }

        var packs = FindPacks(source);
        if (packs.Count == 0)
        {
            items.Add(new Item(source, null, null, "这个文件夹里没有找到音色包（音色包里应该有 voice.json 文件）。\n如果下载的是 zip 压缩包，请先解压。"));
            return new Result(items);
        }
        Directory.CreateDirectory(voices);

        // 先校验所有包、算总大小
        var valid = new List<VoicePack>();
        foreach (var dir in packs)
        {
            var p = VoicePack.TryLoad(dir, out var err);
            if (p == null) items.Add(new Item(dir, null, Path.GetFileName(dir), err));
            else valid.Add(p);
        }

        var existing = VoiceLibrary.Scan(appDir, tidy: false).Packs;
        long total = valid.Sum(p => PackFiles(p).Sum(f => SafeLength(f)));
        long done = 0;

        // 硬盘空间检查（按“全部复制”估算，硬链接只会更省）
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(voices)!);
            if (drive.IsReady && drive.AvailableFreeSpace < total + 50L * 1024 * 1024)
            {
                foreach (var p in valid) items.Add(new Item(p.Dir, null, p.Name,
                    $"硬盘空间不够：需要约 {total / 1048576} MB，剩余 {drive.AvailableFreeSpace / 1048576} MB"));
                return new Result(items);
            }
        }
        catch { /* 网络盘等查不到空间，直接试 */ }

        foreach (var p in valid)
        {
            token.ThrowIfCancellationRequested();
            // 已经有同一个音色了（同名文件夹 + 同一个模型 + 同一个 sid）→ 跳过
            var same = existing.FirstOrDefault(e => e.ModelKey == p.ModelKey && e.Sid == p.Sid);
            if (same != null)
            {
                items.Add(new Item(p.Dir, same.Id, p.Name, null, Skipped: true));
                done += PackFiles(p).Sum(f => SafeLength(f));
                continue;
            }

            string name = VoiceLibrary.SafeFolderName(p.SuggestedId, Path.GetFileName(p.Dir), p.Dir);
            string final = VoiceLibrary.UniqueDir(voices, name);
            string temp = Path.Combine(voices, ".import-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                CopyTree(p.Dir, temp, existing, (bytes, file) =>
                {
                    done += bytes;
                    progress?.Invoke($"正在导入「{p.Name}」…", total > 0 ? Math.Min(1, (double)done / total) : 1);
                }, token);
                var check = VoicePack.TryLoad(temp, out var err2);
                if (check == null) throw new InvalidDataException("复制后校验失败：" + err2);
                Directory.Move(temp, final);
                var imported = VoicePack.TryLoad(final, out _);
                if (imported != null) existing.Add(imported);   // 同一批里的下一个包可以和它共用硬链接
                items.Add(new Item(p.Dir, Path.GetFileName(final), p.Name, null));
                Log.Write($"已导入音色包：{p.Dir} → {final}");
            }
            catch (OperationCanceledException) { TryDelete(temp); throw; }
            catch (Exception ex)
            {
                TryDelete(temp);
                string msg = ex is UnauthorizedAccessException
                    ? "程序所在的文件夹不允许写入（例如放在 C:\\Program Files 里）。请把整个程序文件夹放到 D:\\ 或“文档”等位置后再导入。"
                    : ex.Message;
                items.Add(new Item(p.Dir, null, p.Name, msg));
                Log.Write($"导入音色包失败：{p.Dir}：{ex}");
            }
        }
        return new Result(items);
    }

    public static bool IsPackFile(string path)
    {
        string e = Path.GetExtension(path);
        return e.Equals(".gbvoice", StringComparison.OrdinalIgnoreCase) || e.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private const long MaxExtractBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>安全解压：拒绝绝对路径 / “..” 越界（zip-slip）、过大文件。返回错误说明，成功返回 null。</summary>
    internal static string? SafeExtract(string zipPath, string dest, CancellationToken token)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (Exception) { return "这个文件不是有效的音色包（.gbvoice 其实是 zip 压缩包，这个文件打不开或已损坏）。"; }
        using (zip)
        {
            if (!zip.Entries.Any(e => e.FullName.Replace('\\', '/').EndsWith(VoicePack.ManifestName, StringComparison.OrdinalIgnoreCase)))
                return "压缩包里没有找到 voice.json，不是音色包。";
            if (zip.Entries.Sum(e => e.Length) > MaxExtractBytes) return "音色包解压后超过 4GB，已拒绝。";
            Directory.CreateDirectory(dest);
            string root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var e in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                string rel = e.FullName.Replace('\\', '/');
                if (rel.StartsWith("/") || rel.Contains(':')) return "压缩包里有不安全的路径，已拒绝导入。";
                string target = Path.GetFullPath(Path.Combine(dest, rel));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "压缩包里有不安全的路径，已拒绝导入。";
                if (rel.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: false);
            }
        }
        return null;
    }

    /// <summary>音色包里要复制的文件：整个文件夹的所有文件（模型、词典、voice.json、sample、icon 都在里面）。</summary>
    private static IEnumerable<string> PackFiles(VoicePack p)
    {
        try { return Directory.EnumerateFiles(p.Dir, "*", Walk).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static void CopyTree(string src, string dst, List<VoicePack> existing, Action<long, string> onBytes, CancellationToken token)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", Walk))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", Walk))
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            long len = SafeLength(file);
            if (len >= 1024 * 1024 && TryHardLinkFromExisting(file, len, target, existing))
            {
                onBytes(len, file);
                continue;
            }
            CopyWithProgress(file, target, b => onBytes(b, file), token);
        }
    }

    private static void CopyWithProgress(string src, string dst, Action<long> onBytes, CancellationToken token)
    {
        var buf = new byte[1 << 20];
        using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        using (var o = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            int n;
            while ((n = i.Read(buf, 0, buf.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                o.Write(buf, 0, n);
                onBytes(n);
            }
        }
        if (new FileInfo(dst).Length != new FileInfo(src).Length) throw new IOException("复制不完整：" + Path.GetFileName(src));
    }

    /// <summary>voices 里已有一模一样的文件（大小相同、SHA-256 相同）→ 建硬链接。成功返回 true。</summary>
    private static bool TryHardLinkFromExisting(string src, long len, string target, List<VoicePack> existing)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            string? srcHash = null;
            foreach (var p in existing)
            foreach (var cand in Directory.EnumerateFiles(p.Dir, "*", Walk))
            {
                if (SafeLength(cand) != len) continue;
                srcHash ??= Hash(src);
                if (Hash(cand) != srcHash) continue;
                if (CreateHardLinkW(target, cand, IntPtr.Zero))
                {
                    Log.Write($"导入：{Path.GetFileName(src)} 与已有的 {cand} 相同，用硬链接（不额外占用硬盘）");
                    return true;
                }
                return false;   // 不支持硬链接（FAT32 / 跨盘等）→ 普通复制
            }
        }
        catch (Exception ex) { Log.Write("硬链接检查失败（改为复制）: " + ex.Message); }
        return false;
    }

    private static readonly Dictionary<string, string> HashCache = new(StringComparer.OrdinalIgnoreCase);
    private static string Hash(string path)
    {
        var fi = new FileInfo(path);
        string key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        lock (HashCache) if (HashCache.TryGetValue(key, out var h)) return h;
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        string hex = Convert.ToHexString(SHA256.HashData(s));
        lock (HashCache) HashCache[key] = hex;
        return hex;
    }

    private static long SafeLength(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

    private static IEnumerable<string> SafeDirs(string d)
    {
        try { return Directory.GetDirectories(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase); } catch { return Array.Empty<string>(); }
    }

    private static bool IsUnder(string path, string root)
    {
        string a = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string b = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (Exception ex) { Log.Write("删除临时文件夹失败: " + ex.Message); }
    }

    /// <summary>清理上次导入中途退出留下的临时文件夹（启动时调用）。</summary>
    public static void CleanupTemp(string appDir)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(VoiceLibrary.VoicesDir(appDir), ".import-*")) TryDelete(d);
        }
        catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
