using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SayWhat.Core.Models;
using SayWhat.Engines.Lite;

namespace SayWhat.Tests;

public class LiteMeasurementTests
{
    private const string RunVariable = "SAYWHAT_RUN_MEASUREMENT";
    private const string OutputVariable = "SAYWHAT_MEASUREMENT_OUTPUT";
    private const string ListingUrl = "https://huggingface.co/api/models?author=Xenova&search=opus-mt&limit=300";
    private const long MemoryCeilingBytes = 500L * 1024 * 1024;
    private const int TimedPasses = 3;

    private static readonly Dictionary<string, string[]> Lines = new()
    {
        ["en"] = ["The weather is very nice today.", "Where is the nearest train station?", "Please send me the report by tomorrow morning."],
        ["de"] = ["Das Wetter ist heute sehr schön.", "Wo ist der nächste Bahnhof?", "Bitte schick mir den Bericht bis morgen früh."],
        ["fr"] = ["Il fait très beau aujourd'hui.", "Où est la gare la plus proche ?", "Envoyez-moi le rapport avant demain matin, s'il vous plaît."],
        ["es"] = ["Hoy hace muy buen tiempo.", "¿Dónde está la estación de tren más cercana?", "Por favor, envíame el informe antes de mañana por la mañana."],
        ["it"] = ["Oggi il tempo è molto bello.", "Dov'è la stazione ferroviaria più vicina?", "Per favore, mandami il rapporto entro domani mattina."],
        ["ru"] = ["Сегодня очень хорошая погода.", "Где ближайший вокзал?", "Пожалуйста, пришлите мне отчёт до завтрашнего утра."],
        ["uk"] = ["Сьогодні дуже гарна погода.", "Де найближчий вокзал?", "Будь ласка, надішліть мені звіт до завтрашнього ранку."],
        ["nl"] = ["Het weer is vandaag erg mooi.", "Waar is het dichtstbijzijnde treinstation?", "Stuur me het rapport alsjeblieft voor morgenochtend."],
        ["sv"] = ["Vädret är mycket fint idag.", "Var ligger närmaste tågstation?", "Skicka rapporten till mig före i morgon bitti, tack."],
        ["da"] = ["Vejret er meget flot i dag.", "Hvor er den nærmeste togstation?", "Send mig rapporten inden i morgen tidlig, tak."],
        ["fi"] = ["Sää on tänään erittäin kaunis.", "Missä on lähin rautatieasema?", "Lähetä minulle raportti huomenaamuun mennessä."],
        ["cs"] = ["Dnes je velmi hezké počasí.", "Kde je nejbližší vlakové nádraží?", "Pošlete mi prosím zprávu do zítřejšího rána."],
        ["pl"] = ["Dzisiaj jest bardzo ładna pogoda.", "Gdzie jest najbliższy dworzec kolejowy?", "Proszę przesłać mi raport do jutra rano."],
        ["ro"] = ["Astăzi vremea este foarte frumoasă.", "Unde este cea mai apropiată gară?", "Vă rog să-mi trimiteți raportul până mâine dimineață."],
        ["tr"] = ["Bugün hava çok güzel.", "En yakın tren istasyonu nerede?", "Lütfen raporu bana yarın sabaha kadar gönderin."],
        ["zh"] = ["今天天气非常好。", "最近的火车站在哪里？", "请在明天早上之前把报告发给我。"],
        ["ja"] = ["今日はとても天気がいいです。", "最寄りの駅はどこですか？", "明日の朝までに報告書を送ってください。"],
        ["ko"] = ["오늘은 날씨가 아주 좋습니다.", "가장 가까운 기차역은 어디입니까?", "내일 아침까지 보고서를 보내 주세요."],
        ["ar"] = ["الطقس جميل جدا اليوم.", "أين أقرب محطة قطار؟", "من فضلك أرسل لي التقرير قبل صباح الغد."],
        ["hi"] = ["आज मौसम बहुत अच्छा है।", "सबसे नज़दीकी रेलवे स्टेशन कहाँ है?", "कृपया मुझे कल सुबह तक रिपोर्ट भेज दीजिए।"],
        ["vi"] = ["Hôm nay thời tiết rất đẹp.", "Ga tàu gần nhất ở đâu?", "Vui lòng gửi cho tôi báo cáo trước sáng mai."],
        ["id"] = ["Cuaca hari ini sangat bagus.", "Di mana stasiun kereta terdekat?", "Tolong kirimkan laporan kepada saya sebelum besok pagi."],
        ["hu"] = ["Ma nagyon szép idő van.", "Hol van a legközelebbi vasútállomás?", "Kérem, küldje el nekem a jelentést holnap reggelig."],
    };

    private static readonly Dictionary<string, (string LinesLanguage, string Prefix)> GroupModels = new()
    {
        ["jap-en"] = ("ja", ""),
        ["mul-en"] = ("de", ""),
        ["ROMANCE-en"] = ("fr", ""),
        ["en-ROMANCE"] = ("en", ">>fra<< "),
        ["en-mul"] = ("en", ">>kor<< "),
        ["gem-gem"] = ("en", ">>deu<< "),
        ["gmw-gmw"] = ("en", ">>deu<< "),
        ["en-jap"] = ("en", ""),
    };

    [Fact]
    public async Task Available_pairs_and_per_pair_measurements()
    {
        Assert.SkipUnless(Requested(), "measurement run not requested");
        var report = new StringBuilder();
        AppendMachine(report);
        using var http = new HttpClient();
        var repositories = await ListRepositoriesAsync(http);
        var catalogue = new XenovaModelCatalogue(http);

        report.AppendLine().AppendLine($"## Available repositories ({repositories.Count} listed under Xenova/opus-mt-*, {DateTime.UtcNow:yyyy-MM-dd})").AppendLine();
        report.AppendLine("| Pair | Required-file size (MB) |").AppendLine("| --- | --- |");
        var sizes = new Dictionary<string, long?>();
        foreach (var name in repositories)
        {
            var size = await CatalogueSizeAsync(catalogue, name);
            sizes[name] = size;
            report.AppendLine($"| {name} | {(size is null ? "no required files" : Megabytes(size.Value))} |");
        }

        report.AppendLine().AppendLine("## Per-pair measurement").AppendLine();
        report.AppendLine("Cold load = first load of the pair in this run (files just fetched). Warm load = reload after dispose. Peak memory = largest private-bytes rise over the pre-load baseline during cold load and translation. Per-line = median and maximum over the fixed lines, after one untimed warm-up pass.").AppendLine();
        report.AppendLine("| Pair | Size (MB) | Cold load (ms) | Warm load (ms) | Peak memory (MB) | Resident after (MB) | Line median (ms) | Line max (ms) | Translates | Sample |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var name in repositories.Where(name => sizes[name] is not null))
        {
            report.AppendLine(await MeasurePairAsync(name, sizes[name]!.Value));
        }

        Write("pairs", report);
    }

    [Fact]
    public async Task Pivot_cost_against_direct()
    {
        Assert.SkipUnless(Requested(), "measurement run not requested");
        var report = new StringBuilder();
        AppendMachine(report);
        using var http = new HttpClient();
        var catalogue = new XenovaModelCatalogue(http);

        report.AppendLine().AppendLine("## Pivot through English against a direct pair").AppendLine();
        report.AppendLine($"Memory ceiling under test: {Megabytes(MemoryCeilingBytes)} MB (default `models.memory.ceiling`). Times are `LiteEngine.Translate` wall time per line through `ModelHost`; first = includes model loads, warm = models resident (median of {TimedPasses * 3} calls).").AppendLine();
        report.AppendLine("| Case | Route | Models | On-disk estimate (MB) | Host LoadedBytes (MB) | Private-bytes rise, resident (MB) | First call (ms) | Warm median (ms) | Sample |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        report.AppendLine(await PivotCaseAsync("es to fr direct", ["es-fr"], "es", "fr", catalogue, deny: null));
        report.AppendLine(await PivotCaseAsync("es to fr pivot", ["es-en", "en-fr"], "es", "fr", catalogue, deny: "es-fr"));
        report.AppendLine(await PivotCaseAsync("de to ru pivot (no direct pair)", ["de-en", "en-ru"], "de", "ru", catalogue, deny: null));
        report.AppendLine(await PivotCaseAsync("de to en direct", ["de-en"], "de", "en", catalogue, deny: null));
        report.AppendLine(await PivotCaseAsync("fr to de direct", ["fr-de"], "fr", "de", catalogue, deny: null));
        report.AppendLine(await PivotCaseAsync("fr to de pivot", ["fr-en", "en-de"], "fr", "de", catalogue, deny: "fr-de"));

        Write("pivot", report);
    }

    [Fact]
    public async Task Japanese_and_korean_routes()
    {
        Assert.SkipUnless(Requested(), "measurement run not requested");
        var report = new StringBuilder();
        AppendMachine(report);
        report.AppendLine().AppendLine("## Japanese and Korean routes (OD-023 inputs)").AppendLine();
        using var http = new HttpClient();
        var repositories = await ListRepositoriesAsync(http);
        report.AppendLine($"Repositories with a Korean side: {string.Join(", ", repositories.Where(name => name.Contains("ko", StringComparison.Ordinal) && (name.StartsWith("ko-", StringComparison.Ordinal) || name.EndsWith("-ko", StringComparison.Ordinal))))}");
        report.AppendLine($"Repositories with a Japanese side: {string.Join(", ", repositories.Where(name => name.Contains("jap", StringComparison.Ordinal) || name.StartsWith("ja-", StringComparison.Ordinal) || name.EndsWith("-ja", StringComparison.Ordinal)))}").AppendLine();

        report.AppendLine("| Attempt | Input | Output | Verdict |").AppendLine("| --- | --- | --- | --- |");
        foreach (var (pair, prefix, input, scriptPattern, expectation) in new (string, string, string, string, string)[]
        {
            ("en-jap", "", "Where is the nearest train station?", @"[぀-ヿ一-鿿]", "Japanese script"),
            ("en-jap", "", "The weather is very nice today.", @"[぀-ヿ一-鿿]", "Japanese script"),
            ("en-mul", ">>jpn<< ", "Where is the nearest train station?", @"[぀-ヿ一-鿿]", "Japanese script"),
            ("en-mul", ">>kor<< ", "Where is the nearest train station?", @"[가-힯]", "Hangul"),
            ("en-mul", ">>kor<< ", "The weather is very nice today.", @"[가-힯]", "Hangul"),
        })
        {
            var files = await ModelTestCache.TryGetAllAsync(pair, LiteEngine.RequiredFiles.ToArray());
            if (files is null)
            {
                report.AppendLine($"| {pair} {prefix.Trim()} | {input} | (model files unavailable) | not run |");
                continue;
            }

            using var translator = LitePairTranslator.Load(Path.GetDirectoryName(files[LiteEngine.SourceTokeniserFile])!);
            string output;
            try
            {
                output = translator.Translate(prefix + input);
            }
            catch (Exception exception)
            {
                output = "ERROR: " + exception.Message;
            }

            var verdict = Regex.IsMatch(output, scriptPattern) ? $"{expectation} present" : $"no {expectation}";
            report.AppendLine($"| {pair} {prefix.Trim()} | {input} | {output} | {verdict} |");
        }

        report.AppendLine().AppendLine("Routes to Korean from the listing: a pair `en-ko` or `*-ko` exists only if listed above; Lite pivots through English only (§5.7.1), so a Korean target needs an `en-ko` repository.");
        Write("japanese-korean", report);
    }

    [Fact]
    public void Median_and_megabytes_helpers_format_values()
    {
        Assert.Equal(2.0, Median([3.0, 1.0, 2.0]));
        Assert.Equal(2.5, Median([1.0, 2.0, 3.0, 4.0]));
        Assert.Equal("1.0", Megabytes(1024 * 1024));
    }

    private static bool Requested() => Environment.GetEnvironmentVariable(RunVariable) == "1";

    private static string Megabytes(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string Format(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    private static void Write(string name, StringBuilder report)
    {
        var directory = Environment.GetEnvironmentVariable(OutputVariable) ?? Path.Combine(Path.GetTempPath(), "saywhat-measurement");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"lite-measurement-{name}.md"), report.ToString());
    }

    private static void AppendMachine(StringBuilder report)
    {
        string cpu = "unknown";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString() ?? cpu;
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException)
        {
        }

        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        report.AppendLine("## Machine").AppendLine();
        report.AppendLine($"- CPU: {cpu} ({Environment.ProcessorCount} logical processors)");
        report.AppendLine($"- RAM: {Megabytes(memory)} MB available to the process");
        report.AppendLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        report.AppendLine($"- Runtime: {RuntimeInformation.FrameworkDescription}; ONNX Runtime CPU package 1.32.0 (quantised exports, `{LiteEngine.EncoderFile}`, `{LiteEngine.DecoderFile}`)");
        report.AppendLine($"- Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
    }

    private static async Task<List<string>> ListRepositoriesAsync(HttpClient http)
    {
        using var document = JsonDocument.Parse(await http.GetStringAsync(ListingUrl));
        return document.RootElement.EnumerateArray()
            .Select(model => model.GetProperty("id").GetString()!)
            .Select(id => id["Xenova/opus-mt-".Length..])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<long?> CatalogueSizeAsync(XenovaModelCatalogue catalogue, string name)
    {
        var parts = name.Split('-');
        return parts.Length == 2 ? await catalogue.FindSizeAsync(new LanguagePair(parts[0], parts[1]), default) : null;
    }

    private static async Task<string> MeasurePairAsync(string name, long size)
    {
        var parts = name.Split('-');
        var (linesLanguage, prefix) = GroupModels.TryGetValue(name, out var group) ? group : (parts[0], "");
        var lines = Lines.TryGetValue(linesLanguage, out var found) ? found.Select(line => prefix + line).ToArray() : null;
        try
        {
            var files = await ModelTestCache.TryGetAllAsync(name, LiteEngine.RequiredFiles.ToArray());
            if (files is null)
            {
                return $"| {name} | {Megabytes(size)} | not fetched | | | | | | | |";
            }

            var folder = Path.GetDirectoryName(files[LiteEngine.SourceTokeniserFile])!;
            var diskBytes = files.Values.Sum(path => new FileInfo(path).Length);
            CollectGarbage();
            using var sampler = new MemorySampler();
            var cold = Stopwatch.StartNew();
            var translator = LitePairTranslator.Load(folder);
            cold.Stop();

            var timings = new List<double>();
            var outputs = new List<string>();
            var failure = "";
            if (lines is not null)
            {
                try
                {
                    foreach (var line in lines)
                    {
                        outputs.Add(translator.Translate(line));
                    }

                    for (var pass = 0; pass < TimedPasses; pass++)
                    {
                        foreach (var line in lines)
                        {
                            var watch = Stopwatch.StartNew();
                            translator.Translate(line);
                            timings.Add(watch.Elapsed.TotalMilliseconds);
                        }
                    }
                }
                catch (Exception exception)
                {
                    failure = "ERROR: " + exception.GetType().Name + ": " + exception.Message;
                }
            }

            var resident = sampler.CurrentRise();
            var peak = sampler.PeakRise();
            translator.Dispose();
            CollectGarbage();
            var warm = Stopwatch.StartNew();
            using (LitePairTranslator.Load(folder))
            {
                warm.Stop();
            }

            var translates = failure.Length > 0 ? failure
                : lines is null ? "not run (no fixed lines for this source)"
                : outputs.All(output => !string.IsNullOrWhiteSpace(output)) && outputs.Zip(lines).Any(pair => pair.First != pair.Second) ? "yes"
                : "no (empty or unchanged output)";
            var sample = outputs.Count > 0 ? outputs[0].Replace("|", "/").Replace("\n", " ") : "";
            return $"| {name} | {Megabytes(diskBytes)} | {Format(cold.Elapsed.TotalMilliseconds)} | {Format(warm.Elapsed.TotalMilliseconds)} | {Megabytes(peak)} | {Megabytes(resident)} | {(timings.Count > 0 ? Format(Median(timings)) : "")} | {(timings.Count > 0 ? Format(timings.Max()) : "")} | {translates} | {sample} |";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or ArgumentException)
        {
            return $"| {name} | {Megabytes(size)} | LOAD FAILED: {exception.GetType().Name}: {exception.Message.Replace("|", "/").Replace("\n", " ")} | | | | | | no | |";
        }
    }

    private static async Task<string> PivotCaseAsync(
        string label, string[] pairs, string source, string target, XenovaModelCatalogue catalogue, string? deny)
    {
        var root = Path.Combine(Path.GetTempPath(), "saywhat-measure-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var pair in pairs)
            {
                var files = await ModelTestCache.TryGetAllAsync(pair, LiteEngine.RequiredFiles.ToArray());
                if (files is null)
                {
                    return $"| {label} | {string.Join(" + ", pairs)} | not fetched | | | | | | |";
                }

                foreach (var (file, path) in files)
                {
                    var destination = Path.Combine(root, "lite", pair, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(path, destination);
                }
            }

            using var host = new ModelHost(TimeSpan.FromMinutes(10), MemoryCeilingBytes, TimeProvider.System);
            var engine = new LiteEngine(
                new ModelFolder(root, LiteEngine.RequiredFiles),
                new DenyingCatalogue(catalogue, deny),
                () => host,
                LitePairTranslator.Load,
                TimeSpan.FromSeconds(300));
            var lines = Lines[source];
            CollectGarbage();
            using var sampler = new MemorySampler();

            var first = Stopwatch.StartNew();
            var firstResult = await engine.Translate(lines[0], source, target, default);
            first.Stop();
            if (!firstResult.IsSuccess)
            {
                return $"| {label} | {string.Join(" + ", pairs)} | FAILED: {firstResult.Error?.Category} {firstResult.Error?.Detail} | | | | | | |";
            }

            var timings = new List<double>();
            for (var pass = 0; pass < TimedPasses; pass++)
            {
                foreach (var line in lines)
                {
                    var watch = Stopwatch.StartNew();
                    var result = await engine.Translate(line, source, target, default);
                    timings.Add(watch.Elapsed.TotalMilliseconds);
                    Assert.True(result.IsSuccess, result.Error?.Detail);
                }
            }

            var estimate = pairs.Sum(pair => Directory.EnumerateFiles(Path.Combine(root, "lite", pair), "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length));
            var sample = firstResult.Text!.Replace("|", "/");
            var row = $"| {label} | {source} to {target} | {string.Join(" + ", pairs)} (resident: {string.Join(", ", host.LoadedKeys)}) | {Megabytes(estimate)} | {Megabytes(host.LoadedBytes)} | {Megabytes(sampler.CurrentRise())} | {Format(first.Elapsed.TotalMilliseconds)} | {Format(Median(timings))} | {sample} |";
            await engine.Release();
            return row;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class DenyingCatalogue(ILiteModelCatalogue inner, string? denied) : ILiteModelCatalogue
    {
        public async Task<long?> FindSizeAsync(LanguagePair pair, CancellationToken cancellation)
        {
            if ($"{pair.Source}-{pair.Target}" == denied)
            {
                return null;
            }

            try
            {
                return await inner.FindSizeAsync(pair, cancellation);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return null;
            }
        }
    }

    private sealed class MemorySampler : IDisposable
    {
        private readonly Process process = Process.GetCurrentProcess();
        private readonly long baseline;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private long peak;

        public MemorySampler()
        {
            process.Refresh();
            baseline = process.PrivateMemorySize64;
            peak = baseline;
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    process.Refresh();
                    Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), process.PrivateMemorySize64));
                    await Task.Delay(15);
                }
            });
        }

        public long PeakRise()
        {
            process.Refresh();
            return Math.Max(Interlocked.Read(ref peak), process.PrivateMemorySize64) - baseline;
        }

        public long CurrentRise()
        {
            process.Refresh();
            return process.PrivateMemorySize64 - baseline;
        }

        public void Dispose()
        {
            stop.Cancel();
            try
            {
                loop.Wait();
            }
            catch (AggregateException)
            {
            }

            stop.Dispose();
            process.Dispose();
        }
    }
}
