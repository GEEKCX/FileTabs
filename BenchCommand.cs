using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileTabs;

/// <summary>
/// "--bench" measures the filesystem paths that make FileTabs feel slow.
/// It runs before any WPF object exists and returns without entering the
/// message loop, so blocking on a dedicated thread here is safe and keeps
/// the UI dispatcher out of the numbers.
/// </summary>
internal static class BenchCommand
{
    // Stalls are long-tail events; a mean alone hides them. Five samples is
    // enough to expose p95 without making the bench run for minutes.
    private const int SampleCount = 5;

    private const int FullEnumFiles = 100_000;
    private const int ReducedEnumFiles = 50_000;
    private const int FolderSizeFolders = 1_000;
    private const int FolderSizeFilesPerFolder = 3;
    private const int CollisionFolders = 200;
    private const int MetadataFiles = 10_000;

    // Quick mode is the default: every dimension is shrunk so that creating
    // the data, measuring, and cleaning up all finish within seconds. --full
    // restores the original large sizes for a thorough run.
    private const int QuickEnumFiles = 10_000;
    private const int QuickMetadataFiles = 5_000;
    private const int QuickFolderSizeFolders = 200;
    private const int QuickCollisionFolders = 100;

    // Hard stop so a slow drive can never leave the bench hanging indefinitely.
    // The actual data creation is the slow part, and it polls the token, so the
    // timeout keeps the whole process bounded even on pathological hardware.
    private static readonly TimeSpan FullHardTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QuickHardTimeout = TimeSpan.FromSeconds(60);

    public static int Run(string[] args)
    {
        EnsureConsole();

        var wantJson = false;
        var full = false;
        string? outPath = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    wantJson = true;
                    break;
                case "--full":
                    full = true;
                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        WriteError("--out 缺少输出路径");
                        return 2;
                    }
                    outPath = args[++i];
                    break;
                default:
                    WriteError($"未知参数：{args[i]}");
                    return 2;
            }
        }

        var mode = full ? "full" : "quick";
        var hardTimeout = full ? FullHardTimeout : QuickHardTimeout;
        List<BenchResult> results;
        var runRoot = Path.Combine(
            Path.GetTempPath(), $"filetabs-bench-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(runRoot);
            // A 30-second budget would normally be plenty for the data, but the
            // hard timeout is a guarantee, not a race: on cancellation the token
            // makes data creation bail out, the finally below cleans the temp
            // dir, and we return non-zero instead of hanging.
            using var cts = new CancellationTokenSource(hardTimeout);
            results = Task.Run(() => MeasureAll(runRoot, full, cts.Token))
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            WriteError($"基准测试超过硬超时（{hardTimeout.TotalSeconds:F0}s），已中止");
            return 1;
        }
        catch (AggregateException ex) when (
            ex.InnerException is OperationCanceledException)
        {
            WriteError($"基准测试超过硬超时（{hardTimeout.TotalSeconds:F0}s），已中止");
            return 1;
        }
        catch (Exception ex)
        {
            WriteError($"基准测试失败：{ex}");
            return 1;
        }
        finally
        {
            TryDelete(runRoot);
        }

        var report = new BenchReport(
            ProductVersion(), RuntimeLabel(), DateTimeOffset.Now, mode, results);
        var text = wantJson ? report.ToJson() : report.ToText();

        if (outPath is not null)
        {
            try
            {
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                WriteError($"无法写入 {outPath}：{ex.Message}");
                return 1;
            }
        }

        Console.Write(text);
        Console.Out.Flush();
        return results.Any(result => result.Error is not null) ? 1 : 0;
    }

    private static List<BenchResult> MeasureAll(
        string runRoot, bool full, CancellationToken token)
    {
        ThreadGuard.AssertBackground(nameof(MeasureAll));

        var results = new List<BenchResult>();

        // The two enumeration items share one backing directory: build it once
        // so we don't pay to create the same files twice. The setup cost is
        // charged to the first item; the cached item reports setup=0 honestly
        // because its data was already on disk.
        var enumCount = full ? ChooseEnumFileCount() : QuickEnumFiles;
        var enumDir = NewTempDir(runRoot, "enum");
        var enumSetupMs = 0d;
        {
            var setupSw = Stopwatch.StartNew();
            CreateEmptyFiles(enumDir, enumCount, token);
            enumSetupMs = setupSw.Elapsed.TotalMilliseconds;
        }

        RunItem(results, 1, 5, $"dir_enum_{CompactCount(enumCount)}",
            r => MeasureDirEnum(enumDir, enumCount, enumSetupMs, r, token));
        RunItem(results, 2, 5, $"dir_enum_{CompactCount(enumCount)}_cached",
            r => MeasureDirEnumCached(enumDir, enumCount, r, token));

        var sizeFolders = full ? FolderSizeFolders : QuickFolderSizeFolders;
        RunItem(results, 3, 5, $"folder_size_{CompactCount(sizeFolders)}",
            r => MeasureFolderSize(runRoot, sizeFolders, r, token));

        var collisionCount = full ? CollisionFolders : QuickCollisionFolders;
        RunItem(results, 4, 5, $"newfolder_collision_{CompactCount(collisionCount)}",
            r => MeasureNewFolderCollision(runRoot, collisionCount, r, token));

        var metaCount = full ? MetadataFiles : QuickMetadataFiles;
        RunItem(results, 5, 5, $"fs_meta_stat_{CompactCount(metaCount)}",
            r => MeasureMetadata(runRoot, metaCount, r, token));

        return results;
    }

    /// <summary>
    /// Runs one bench item with progress to stderr (visible even in --json,
    /// since JSON goes to stdout) and an all-or-nothing failure mode: an item
    /// failing alone must not abort the rest of the run, but a timeout must
    /// stop everything and let the caller clean up.
    /// </summary>
    private static void RunItem(
        List<BenchResult> results,
        int index,
        int total,
        string name,
        Action<BenchResult> body)
    {
        WriteError($"[{index}/{total}] {name} ...");
        var result = new BenchResult(name);
        try
        {
            body(result);
        }
        catch (OperationCanceledException)
        {
            // Hard timeout: stop the whole run, not just this item.
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
        }
        results.Add(result);
        var setup = result.SetupMs is { } sm ? $"{sm:F0}ms" : "-";
        var measure = result.Mean is { } m ? $"measure {m:F0}ms" : "measure -";
        WriteError($"[{index}/{total}] {name} ... done (setup {setup}, {measure})");
    }

    /// <summary>Compacts large numbers for item names: 100000 → 100k, 5000 → 5k.</summary>
    private static string CompactCount(int value)
    {
        return value >= 1_000 && value % 1_000 == 0
            ? $"{value / 1_000}k"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static void MeasureDirEnum(
        string dir, int count, double setupMs, BenchResult result, CancellationToken token)
    {
        // The data was created once in MeasureAll and shared with the cached
        // item; charge the creation time to this item.
        result.SetupMs = setupMs;
        result.Count = count;

        for (var i = 0; i < SampleCount; i++)
        {
            token.ThrowIfCancellationRequested();
            result.Add(Measure(() => CountEntries(dir)));
        }
    }

    private static void MeasureDirEnumCached(
        string dir, int count, BenchResult result, CancellationToken token)
    {
        // Reuses the same directory MeasureDirEnum already built, so setup is 0
        // and the cached item measures only the cache-hit path.
        result.SetupMs = 0;
        result.Count = count;

        var key = Path.GetFullPath(dir);
        DirectoryListingCache.Invalidate(key);
        try
        {
            // Populate through the same entry point the address bar uses.
            var scan = DirectoryListingCache.GetOrScan(
                key,
                () => new DirectoryListingScan(EnumerateEntries(dir), Complete: true));
            if (scan.Entries.Count != count)
            {
                throw new InvalidOperationException(
                    $"缓存条目数 {scan.Entries.Count} 与文件数 {count} 不一致");
            }

            for (var i = 0; i < SampleCount; i++)
            {
                result.Add(Measure(() =>
                {
                    var cached = DirectoryListingCache.TryGet(key)
                        ?? throw new InvalidOperationException("缓存条目提前过期");
                    if (cached.Count != count)
                    {
                        throw new InvalidOperationException(
                            $"缓存条目数 {cached.Count} 与文件数 {count} 不一致");
                    }
                }));
            }
            var hitNote = $"{count:N0} 条列表命中缓存";
            result.Note = result.Note is null ? hitNote : $"{result.Note}；{hitNote}";
        }
        finally
        {
            DirectoryListingCache.Invalidate(key);
        }
    }

    private static void MeasureFolderSize(
        string runRoot, int folders, BenchResult result, CancellationToken token)
    {
        var dir = NewTempDir(runRoot, "sizes");
        var payload = new byte[1024];
        {
            var setup = Stopwatch.StartNew();
            for (var i = 0; i < folders; i++)
            {
                token.ThrowIfCancellationRequested();
                var sub = Path.Combine(dir, $"folder-{i:D4}");
                Directory.CreateDirectory(sub);
                for (var j = 0; j < FolderSizeFilesPerFolder; j++)
                {
                    File.WriteAllBytes(Path.Combine(sub, $"f{j}.bin"), payload);
                }
            }
            result.SetupMs = setup.Elapsed.TotalMilliseconds;
        }
        result.Count = folders;

        for (var i = 0; i < SampleCount; i++)
        {
            // Same recursive enumeration FolderSizeCache uses for the status bar.
            result.Add(Measure(() => FolderSizeCache.ComputeSize(dir)));
        }
    }

    private static void MeasureNewFolderCollision(
        string runRoot, int folders, BenchResult result, CancellationToken token)
    {
        var dir = NewTempDir(runRoot, "collision");
        // The base name plus (2)..(N) leaves N taken names, so the probe has to
        // walk the whole sequence before finding a free suffix.
        {
            var setup = Stopwatch.StartNew();
            Directory.CreateDirectory(Path.Combine(dir, "新建文件夹"));
            for (var i = 2; i <= folders; i++)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(dir, $"新建文件夹 ({i})"));
            }
            result.SetupMs = setup.Elapsed.TotalMilliseconds;
        }
        result.Count = folders;

        for (var i = 0; i < SampleCount; i++)
        {
            result.Add(Measure(() =>
            {
                var candidate = NewFolderProbe.FindAvailableName(dir);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    throw new InvalidOperationException($"探测返回了已存在的路径：{candidate}");
                }
            }));
        }
    }

    private static void MeasureMetadata(
        string runRoot, int files, BenchResult result, CancellationToken token)
    {
        var dir = NewTempDir(runRoot, "meta");
        double setupMs;
        {
            var setup = Stopwatch.StartNew();
            CreateEmptyFiles(dir, files, token);
            setupMs = setup.Elapsed.TotalMilliseconds;
        }
        result.SetupMs = setupMs;
        var paths = Directory.GetFiles(dir);
        if (paths.Length != files)
        {
            throw new InvalidOperationException(
                $"文件数 {paths.Length} 与预期 {files} 不一致");
        }
        result.Count = files;

        for (var i = 0; i < SampleCount; i++)
        {
            result.Add(Measure(() =>
            {
                long total = 0;
                var newest = DateTime.MinValue;
                foreach (var path in paths)
                {
                    // A fresh FileInfo per sample: reusing instances would
                    // serve cached properties from memory instead of disk.
                    var info = new FileInfo(path);
                    total += info.Length;
                    if (info.LastWriteTimeUtc > newest) newest = info.LastWriteTimeUtc;
                }
                GC.KeepAlive(newest);
                if (total < 0) throw new InvalidOperationException();
            }));
        }
    }

    private static double Measure(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static long CountEntries(string dir)
    {
        ThreadGuard.AssertBackground(nameof(CountEntries));
        long count = 0;
        foreach (var _ in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            count++;
        }
        return count;
    }

    private static List<DirectoryListingEntry> EnumerateEntries(string dir)
    {
        ThreadGuard.AssertBackground(nameof(EnumerateEntries));
        var entries = new List<DirectoryListingEntry>();
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            entries.Add(new DirectoryListingEntry(
                info.FullName, info.Name, info is DirectoryInfo));
        }
        return entries;
    }

    private static int ChooseEnumFileCount()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            if (root is null) return FullEnumFiles;
            var drive = new DriveInfo(root);
            // Empty files consume almost no data blocks, but NTFS still needs
            // metadata space; keep a wide margin so the bench never fills the
            // disk. Report the reduced count instead of silently doing less.
            return drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024
                ? ReducedEnumFiles
                : FullEnumFiles;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            return ReducedEnumFiles;
        }
    }

    private static void CreateEmptyFiles(string dir, int count, CancellationToken token)
    {
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            // File.Create truncates, so a retry after a failed item is safe.
            using var _ = File.Create(Path.Combine(dir, $"f{i:D6}.tmp"));
        }
    }

    private static string NewTempDir(string runRoot, string name)
    {
        var dir = Path.Combine(runRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteError($"临时目录未能清理：{root}（{ex.Message}）");
        }
    }

    private static string ProductVersion()
    {
        var informational = typeof(BenchCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // SDK builds append "+<commit>"; that is noise in a report header.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        var version = typeof(BenchCommand).Assembly.GetName().Version;
        return version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string RuntimeLabel()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        return string.IsNullOrEmpty(rid)
            ? $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}"
            : rid;
    }

    // WinExe has no console of its own: attach to the launching shell so
    // `FileTabs.exe --bench` prints, or allocate one when started from
    // Explorer. Redirected stdout keeps working either way.
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static void EnsureConsole()
    {
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                if (!AttachConsole(AttachParentProcess)) AllocConsole();
            }
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (IOException)
        {
            // No usable console; --out still produces the report.
        }
    }

    private static void WriteError(string message)
    {
        Console.Error.WriteLine(message);
    }

    private sealed class BenchResult
    {
        private readonly List<double> _samples = new();

        public BenchResult(string name) => Name = name;

        public string Name { get; }
        public int? Count { get; set; }
        public double? SetupMs { get; set; }
        public string? Note { get; set; }
        public string? Error { get; set; }
        public IReadOnlyList<double> Samples => _samples;

        public double? Mean => _samples.Count == 0 ? null : _samples.Average();
        public double? P50 => Percentile(50);
        public double? P95 => Percentile(95);
        public double? Min => _samples.Count == 0 ? null : _samples.Min();
        public double? Max => _samples.Count == 0 ? null : _samples.Max();

        public void Add(double milliseconds) => _samples.Add(milliseconds);

        private double? Percentile(double percent)
        {
            if (_samples.Count == 0) return null;
            // Nearest-rank: p95 of five samples is the largest one, which is
            // exactly the tail we want visible rather than smoothed away.
            var sorted = _samples.OrderBy(value => value).ToArray();
            var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Length);
            return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
        }
    }

    private sealed record BenchReport(
        string Version,
        string Runtime,
        DateTimeOffset Timestamp,
        string Mode,
        IReadOnlyList<BenchResult> Results)
    {
        public string ToText()
        {
            var builder = new StringBuilder();
            builder.AppendLine(
                $"FileTabs benchmark  ({Version}, {Runtime}, mode={Mode}, {Timestamp:yyyy-MM-dd HH:mm:ss})");
            builder.AppendLine(new string('-', 72));
            builder.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0,-24}{1,10}{2,9}{3,9}{4,9}{5,9}{6,9}",
                "item", "setup", "mean", "p50", "p95", "min", "max"));
            // setup is the time to build the test data; measure columns are
            // purely the sampled operation. A big setup next to a small mean is
            // expected on slow drives — the two must never be conflated.

            foreach (var result in Results)
            {
                if (result.Error is not null)
                {
                    builder.AppendLine($"{result.Name,-24}ERROR: {result.Error}");
                    continue;
                }
                builder.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-24}{1,10}{2,9}{3,9}{4,9}{5,9}{6,9}",
                    result.Name,
                    FormatMs(result.SetupMs),
                    FormatMs(result.Mean),
                    FormatMs(result.P50),
                    FormatMs(result.P95),
                    FormatMs(result.Min),
                    FormatMs(result.Max)));
                if (result.Note is not null)
                {
                    builder.AppendLine($"{"",-24}note: {result.Note}");
                }
            }
            return builder.ToString();
        }

        public string ToJson()
        {
            var payload = new
            {
                tool = "FileTabs",
                version = Version,
                runtime = Runtime,
                mode = Mode,
                timestamp = Timestamp.ToString(
                    "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                items = Results.Select(result => new
                {
                    name = result.Name,
                    count = result.Count,
                    setup_ms = Round(result.SetupMs),
                    samples = result.Samples.Count,
                    mean = Round(result.Mean),
                    p50 = Round(result.P50),
                    p95 = Round(result.P95),
                    min = Round(result.Min),
                    max = Round(result.Max),
                    note = result.Note,
                    error = result.Error,
                }),
            };
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                // Chinese notes must stay readable in the report.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + Environment.NewLine;
        }

        private static string FormatMs(double? value)
        {
            return value is { } ms
                ? ms.ToString("F1", CultureInfo.InvariantCulture) + "ms"
                : "-";
        }

        private static double? Round(double? value)
        {
            return value is { } ms ? Math.Round(ms, 1) : null;
        }
    }
}
