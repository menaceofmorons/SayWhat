using SayWhat.Core.Lines;
using SayWhat.Core.LogIngest;

namespace SayWhat.Tests.Lines;

public class SessionLineStoreTests
{
    [Fact]
    public void AddKeepsArrivalOrderAndRaisesOneEvent()
    {
        var store = new SessionLineStore();
        var changes = new List<LineChange>();
        store.Changed += changes.Add;

        Assert.True(store.Add(LineTestData.Make(3)));
        Assert.True(store.Add(LineTestData.Make(1)));

        Assert.Equal([3, 1], store.Snapshot().Select(l => l.LineId.Sequence));
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(LineChangeKind.Added, c.Kind));
    }

    [Fact]
    public void DuplicateAddIsRefusedWithoutEvent()
    {
        var store = new SessionLineStore();
        store.Add(LineTestData.Make(1));
        var raised = 0;
        store.Changed += _ => raised++;

        Assert.False(store.Add(LineTestData.Make(1)));
        Assert.Equal(0, raised);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void UpdateByLineIdKeepsDisplayOrder()
    {
        var store = new SessionLineStore();
        var changes = new List<LineChange>();
        store.Add(LineTestData.Make(1));
        store.Add(LineTestData.Make(2));
        store.Add(LineTestData.Make(3));
        store.Changed += changes.Add;

        var result = store.Update(
            LineTestData.Make(1).LineId,
            l => l with { State = LineState.Translated, TranslatedText = "hello" });

        Assert.Equal(LineUpdateResult.Applied, result);
        Assert.Equal([1, 2, 3], store.Snapshot().Select(l => l.LineId.Sequence));
        Assert.Equal("hello", store.Snapshot()[0].TranslatedText);
        var change = Assert.Single(changes);
        Assert.Equal(LineChangeKind.Updated, change.Kind);
        Assert.True(store.TryGet(change.Line.LineId, out var stored));
        Assert.Equal(LineState.Translated, stored.State);
    }

    [Fact]
    public void UnknownLineIdIsNotFound()
    {
        var store = new SessionLineStore();
        Assert.Equal(LineUpdateResult.NotFound, store.Update(new LineId(9, 9), l => l));
        Assert.False(store.TryGet(new LineId(9, 9), out _));
    }

    [Theory]
    [InlineData(LineState.PassThrough)]
    [InlineData(LineState.PassThroughTranslationOff)]
    [InlineData(LineState.LanguageUnknown)]
    [InlineData(LineState.SameLanguage)]
    [InlineData(LineState.Translated)]
    public void UpdateToFinalLineIsRefused(LineState state)
    {
        var store = new SessionLineStore();
        var line = LineTestData.Make(1, state);
        store.Add(line);
        var raised = 0;
        store.Changed += _ => raised++;

        var result = store.Update(line.LineId, l => l with { State = LineState.Failed });

        Assert.Equal(LineUpdateResult.RefusedFinal, result);
        Assert.Equal(state, store.Snapshot()[0].State);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ResultAfterFailedIsDiscarded()
    {
        var store = new SessionLineStore();
        var line = LineTestData.Make(1);
        store.Add(line);
        store.Update(line.LineId, l => l with { State = LineState.Failed });
        var raised = 0;
        store.Changed += _ => raised++;

        var result = store.Update(line.LineId, l => l with { State = LineState.Translated, TranslatedText = "late" });

        Assert.Equal(LineUpdateResult.DiscardedAfterFailed, result);
        Assert.Equal(LineState.Failed, store.Snapshot()[0].State);
        Assert.Null(store.Snapshot()[0].TranslatedText);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ChangeToDisallowedStateOrIdIsRefused()
    {
        var store = new SessionLineStore();
        var line = LineTestData.Make(1);
        store.Add(line);

        Assert.Equal(
            LineUpdateResult.RefusedTransition,
            store.Update(line.LineId, l => l with { State = LineState.SameLanguage }));
        Assert.Equal(
            LineUpdateResult.RefusedTransition,
            store.Update(line.LineId, l => l with { LineId = new LineId(2, 2) }));
        Assert.Equal(LineState.Pending, store.Snapshot()[0].State);
    }

    [Fact]
    public async Task ConcurrentAddsAndUpdatesKeepEveryLineOnceWithOneEventEach()
    {
        var store = new SessionLineStore();
        var events = 0;
        store.Changed += _ => Interlocked.Increment(ref events);
        const int count = 400;

        await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() =>
        {
            var line = LineTestData.Make(i);
            store.Add(line);
            store.Update(line.LineId, l => l with { State = LineState.Translated, TranslatedText = "t" });
        })));

        var lines = store.Snapshot();
        Assert.Equal(count, lines.Count);
        Assert.Equal(count, lines.Select(l => l.LineId).Distinct().Count());
        Assert.All(lines, l => Assert.Equal(LineState.Translated, l.State));
        Assert.Equal(count * 2, events);
    }
}
