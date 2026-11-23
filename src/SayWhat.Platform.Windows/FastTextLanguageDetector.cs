using Panlingo.LanguageIdentification.FastText;
using SayWhat.Core.LanguageDetection;

namespace SayWhat.Platform.Windows;

public sealed class FastTextLanguageDetector : ILanguageDetector, IDisposable
{
    private const string LabelPrefix = "__label__";
    private const int PredictionCount = 200;

    private readonly object gate = new();
    private FastTextDetector? detector;

    public FastTextLanguageDetector(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            return;
        }

        var loaded = new FastTextDetector();
        try
        {
            loaded.LoadModel(modelPath);
            detector = loaded;
        }
        catch
        {
            loaded.Dispose();
        }
    }

    public bool IsReady
    {
        get
        {
            lock (gate)
            {
                return detector is not null;
            }
        }
    }

    public DetectionResult Detect(string text)
    {
        var script = ScriptCheck.Check(text).DominantScript;
        if (string.IsNullOrWhiteSpace(text))
        {
            return DetectionResult.Unknown(script);
        }

        var line = text.ReplaceLineEndings(" ");
        var candidates = ScriptCandidates.For(script);
        lock (gate)
        {
            if (detector is null)
            {
                return DetectionResult.Unknown(script);
            }

            foreach (var prediction in detector.Predict(line, PredictionCount, 0f))
            {
                var code = prediction.Label.StartsWith(LabelPrefix, StringComparison.Ordinal)
                    ? prediction.Label[LabelPrefix.Length..]
                    : prediction.Label;
                if (candidates is null || candidates.Contains(code))
                {
                    return new DetectionResult(code, Math.Clamp(prediction.Probability, 0d, 1d), script);
                }
            }
        }

        return DetectionResult.Unknown(script);
    }

    public void Dispose()
    {
        lock (gate)
        {
            detector?.Dispose();
            detector = null;
        }
    }
}
