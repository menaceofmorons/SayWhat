using SayWhat.Core.Engines;
using SayWhat.Core.Lines;
using SayWhat.Core.LogIngest;

namespace SayWhat.Core.Pipeline;

public sealed record TranslateOutcome(LineId LineId, LineState State, EngineErrorCategory? Error);
