namespace SayWhat.Engines.Lite;

public interface ILitePairTranslator : IDisposable
{
    string Translate(string text);
}
