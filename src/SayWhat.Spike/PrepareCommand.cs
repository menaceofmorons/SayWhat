using SayWhat.Core.Models;
using SayWhat.Core.ReferenceData;
using SayWhat.Engines.Lite;

namespace SayWhat.Spike;

// TEMP: spike-only asset download; remove when the PH-2 Download Manager is in place.
public sealed record PrepareSources(string SdeTableUrl, string DetectorUrl, string PairFileUrl)
{
    public static PrepareSources Published { get; } = new(
        "https://sde.evetech.net/tranquility/latest/{0}.jsonl",
        "https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.ftz",
        "https://huggingface.co/Xenova/opus-mt-{0}-{1}/resolve/main/{2}");
}

public sealed record PrepareOptions(string ModelFolder, string Target, IReadOnlyList<string>? Sources)
{
    public const string DefaultTarget = "en";

    public static string DefaultModelFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SayWhat", "Models");

    public static IReadOnlyList<string> ClientLanguages { get; } = ["en", "de", "fr", "es", "ru", "ja", "ko", "zh"];
}

public sealed class PrepareException(string message, Exception? inner = null) : Exception(message, inner);

// TEMP: spike-only asset download; remove when the PH-2 Download Manager is in place.
public sealed class PrepareCommand(HttpClient client, PrepareSources sources, TextWriter output, TextWriter error)
{
    public const string ReferenceDatabaseFile = "reference.db";
    public const string DetectorFile = "lid.176.ftz";

    private const string PartialSuffix = ".partial";
    private const string SdeWorkFolder = "sde-work";
    private static readonly string[] SdeTableNames =
        [SdeTables.Build, SdeTables.Groups, SdeTables.Types, SdeTables.SolarSystems, SdeTables.Languages];

    public async Task<int> RunAsync(PrepareOptions options, CancellationToken cancellation = default)
    {
        try
        {
            Directory.CreateDirectory(options.ModelFolder);
            await PrepareReferenceDatabaseAsync(options.ModelFolder, cancellation).ConfigureAwait(false);
            await PrepareDetectorAsync(options.ModelFolder, cancellation).ConfigureAwait(false);
            await PreparePairsAsync(options, cancellation).ConfigureAwait(false);
            return 0;
        }
        catch (PrepareException exception)
        {
            error.WriteLine($"prepare failed: {exception.Message}");
            return 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or SdeImportException)
        {
            error.WriteLine($"prepare failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private async Task PrepareReferenceDatabaseAsync(string folder, CancellationToken cancellation)
    {
        var databasePath = Path.Combine(folder, ReferenceDatabaseFile);
        if (File.Exists(databasePath))
        {
            output.WriteLine($"skip {ReferenceDatabaseFile}: present");
            return;
        }

        var work = Path.Combine(folder, SdeWorkFolder);
        Directory.CreateDirectory(work);
        try
        {
            foreach (var table in SdeTableNames)
            {
                await DownloadAsync(string.Format(sources.SdeTableUrl, table), Path.Combine(work, table + ".jsonl"), cancellation).ConfigureAwait(false);
            }

            output.WriteLine($"import {ReferenceDatabaseFile}");
            new SdeImporter(new DirectorySdeSource(work)).Import(databasePath, cancellation);
            output.WriteLine($"done {ReferenceDatabaseFile}");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private async Task PrepareDetectorAsync(string folder, CancellationToken cancellation)
    {
        var path = Path.Combine(folder, DetectorFile);
        if (File.Exists(path))
        {
            output.WriteLine($"skip {DetectorFile}: present");
            return;
        }

        await DownloadAsync(sources.DetectorUrl, path, cancellation).ConfigureAwait(false);
        output.WriteLine($"done {DetectorFile}");
    }

    private async Task PreparePairsAsync(PrepareOptions options, CancellationToken cancellation)
    {
        var modelFolder = new ModelFolder(options.ModelFolder, LiteEngine.RequiredFiles);
        var catalogue = new XenovaModelCatalogue(client);
        var candidates = options.Sources ?? PrepareOptions.ClientLanguages;
        foreach (var source in candidates.Where(language => language != options.Target))
        {
            var pair = new LanguagePair(source, options.Target);
            if (modelFolder.IsComplete(pair))
            {
                output.WriteLine($"skip {source}-{options.Target}: present");
                continue;
            }

            if (await catalogue.FindSizeAsync(pair, cancellation).ConfigureAwait(false) is null)
            {
                output.WriteLine($"skip {source}-{options.Target}: no published model");
                continue;
            }

            var directory = modelFolder.PathFor(pair);
            foreach (var file in LiteEngine.RequiredFiles)
            {
                var url = string.Format(sources.PairFileUrl, Uri.EscapeDataString(source), Uri.EscapeDataString(options.Target), file);
                await DownloadAsync(url, Path.Combine(directory, file), cancellation).ConfigureAwait(false);
            }

            output.WriteLine($"done {source}-{options.Target}");
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new PrepareException($"refused non-HTTPS source {url}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + PartialSuffix;
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var expected = response.Content.Headers.ContentLength;
            long received;
            await using (var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
            await using (var target = File.Create(partial))
            {
                await body.CopyToAsync(target, cancellation).ConfigureAwait(false);
                received = target.Length;
            }

            if (expected is { } length && received != length)
            {
                throw new PrepareException($"{url} ended after {received} of {length} bytes");
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch (PrepareException)
        {
            DeleteIfPresent(partial);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            DeleteIfPresent(partial);
            throw new PrepareException($"{url}: {exception.Message}", exception);
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
