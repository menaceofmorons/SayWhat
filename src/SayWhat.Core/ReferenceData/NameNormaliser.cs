using System.Text;

namespace SayWhat.Core.ReferenceData;

public static class NameNormaliser
{
    public static string Normalise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }
}
