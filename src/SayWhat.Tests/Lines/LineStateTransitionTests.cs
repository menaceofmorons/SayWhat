using SayWhat.Core.Lines;

namespace SayWhat.Tests.Lines;

public class LineStateTransitionTests
{
    public static TheoryData<LineState, LineState, bool> Cases()
    {
        var data = new TheoryData<LineState, LineState, bool>();
        foreach (var from in Enum.GetValues<LineState>())
        {
            foreach (var to in Enum.GetValues<LineState>())
            {
                var allowed = from == LineState.Pending
                    && (to == LineState.Pending || to == LineState.Translated || to == LineState.Failed);
                data.Add(from, to, allowed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TransitionMatchesFigure3(LineState from, LineState to, bool allowed) =>
        Assert.Equal(allowed, LineStates.CanTransition(from, to));

    [Theory]
    [InlineData(LineState.PassThrough, true)]
    [InlineData(LineState.PassThroughTranslationOff, true)]
    [InlineData(LineState.LanguageUnknown, true)]
    [InlineData(LineState.SameLanguage, true)]
    [InlineData(LineState.Pending, false)]
    [InlineData(LineState.Translated, true)]
    [InlineData(LineState.Failed, true)]
    public void OnlyPendingIsNotFinal(LineState state, bool final) =>
        Assert.Equal(final, LineStates.IsFinal(state));
}
