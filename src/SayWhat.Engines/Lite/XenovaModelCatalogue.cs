using System.Net;
using System.Text.Json;
using SayWhat.Core.Models;

namespace SayWhat.Engines.Lite;

public sealed class XenovaModelCatalogue(HttpClient client) : ILiteModelCatalogue
{
    private const string ListingUrl = "https://huggingface.co/api/models/Xenova/opus-mt-{0}-{1}?blobs=true";

    public async Task<long?> FindSizeAsync(LanguagePair pair, CancellationToken cancellation)
    {
        using var response = await client.GetAsync(
            string.Format(ListingUrl, Uri.EscapeDataString(pair.Source), Uri.EscapeDataString(pair.Target)), cancellation).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellation).ConfigureAwait(false);
        var sizes = new Dictionary<string, long>();
        if (document.RootElement.TryGetProperty("siblings", out var siblings))
        {
            foreach (var sibling in siblings.EnumerateArray())
            {
                if (sibling.TryGetProperty("rfilename", out var name)
                    && sibling.TryGetProperty("size", out var size)
                    && size.ValueKind == JsonValueKind.Number)
                {
                    sizes[name.GetString()!] = size.GetInt64();
                }
            }
        }

        long total = 0;
        foreach (var file in LiteEngine.RequiredFiles)
        {
            if (!sizes.TryGetValue(file, out var length))
            {
                return null;
            }

            total += length;
        }

        return total;
    }
}
