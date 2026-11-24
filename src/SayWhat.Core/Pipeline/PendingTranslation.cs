using SayWhat.Core.Lines;
using SayWhat.Core.TermProtection;

namespace SayWhat.Core.Pipeline;

public sealed record PendingTranslation(Line Line, ProtectedLine Protected, string SourceLanguage, string TargetLanguage);
