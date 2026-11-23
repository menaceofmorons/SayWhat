namespace SayWhat.Core.LanguageDetection;

public interface ILanguageDetector
{
    bool IsReady { get; }

    DetectionResult Detect(string text);
}
