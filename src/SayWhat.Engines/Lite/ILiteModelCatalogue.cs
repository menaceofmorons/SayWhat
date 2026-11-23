using SayWhat.Core.Models;

namespace SayWhat.Engines.Lite;

public interface ILiteModelCatalogue
{
    Task<long?> FindSizeAsync(LanguagePair pair, CancellationToken cancellation);
}
