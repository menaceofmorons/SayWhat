using System.Text;
using SayWhat.Core.ReferenceData;

namespace SayWhat.Tests;

[Collection(SerialMemoryCollection.Name)]
public sealed class SdeImportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "saywhat-sde-" + Guid.NewGuid().ToString("N"));
    private readonly SdeImportOptions small = new() { MinimumShipTypes = 3, MinimumSolarSystems = 3 };

    public SdeImportTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string DatabasePath => Path.Combine(directory, "reference.db");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_KeepsOnlyShipsAndEverySystem(bool gzip)
    {
        var result = new SdeImporter(SyntheticSde.Create(gzip: gzip), small).Import(DatabasePath);

        Assert.Equal(new SdeImportResult("4242", 3, 24, 3, 24), result);
        var database = new ReferenceDatabase(DatabasePath);
        Assert.Equal("4242", database.SdeVersion);
        Assert.Equal(SyntheticSde.Languages, database.ClientLanguages());
        Assert.Equal(3, database.ShipNames("en").Count);
        Assert.Null(database.FindShipType("en", "Module 9001"));
        Assert.False(File.Exists(DatabasePath + ".new"));
    }

    [Fact]
    public void Import_BelowMinimumShipCount_KeepsThePreviousDatabase()
    {
        new SdeImporter(SyntheticSde.Create(ships: 3), small).Import(DatabasePath);
        var before = File.ReadAllBytes(DatabasePath);

        var failing = new SdeImporter(SyntheticSde.Create(ships: 2), small);

        Assert.Throws<SdeImportException>(() => failing.Import(DatabasePath));
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        Assert.False(File.Exists(DatabasePath + ".new"));
        Assert.Equal(3, new ReferenceDatabase(DatabasePath).ShipNames("en").Count);
    }

    [Fact]
    public void Import_BelowMinimumSystemCount_Fails()
    {
        var importer = new SdeImporter(SyntheticSde.Create(systems: 2), small);

        Assert.Throws<SdeImportException>(() => importer.Import(DatabasePath));
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public void Import_WhenALanguageHasNoShipNames_Fails()
    {
        var sde = SyntheticSde.Create();
        var lines = Enumerable.Range(0, 3)
            .Select(i => SyntheticSde.ShipLine(600 + i, 25, code => code == "zh" ? string.Empty : $"S{i}-{code}"))
            .ToArray();
        sde.Set("types", false, lines);

        var importer = new SdeImporter(sde, small);

        var error = Assert.Throws<SdeImportException>(() => importer.Import(DatabasePath));
        Assert.Contains("zh", error.Message);
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public void Import_Interrupted_KeepsThePreviousDatabaseAndLeavesNoPendingFile()
    {
        new SdeImporter(SyntheticSde.Create(), small).Import(DatabasePath);
        var before = File.ReadAllBytes(DatabasePath);
        var sde = SyntheticSde.Create();
        sde.SetStream("types", () => new FaultingStream(Encoding.UTF8.GetBytes(SyntheticSde.ShipLine(700, 25, c => "x" + c) + "\n")));

        var error = Assert.Throws<SdeImportException>(() => new SdeImporter(sde, small).Import(DatabasePath));

        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
        Assert.False(File.Exists(DatabasePath + ".new"));
    }

    [Fact]
    public void Import_Cancelled_KeepsThePreviousDatabase()
    {
        new SdeImporter(SyntheticSde.Create(), small).Import(DatabasePath);
        var before = File.ReadAllBytes(DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new SdeImporter(SyntheticSde.Create(), small).Import(DatabasePath, cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(DatabasePath));
    }

    [Fact]
    public void Import_PassingImportReplacesThePreviousDatabaseWhole()
    {
        new SdeImporter(SyntheticSde.Create(ships: 3), small).Import(DatabasePath);

        new SdeImporter(SyntheticSde.Create(ships: 5), small).Import(DatabasePath);

        Assert.Equal(5, new ReferenceDatabase(DatabasePath).ShipNames("de").Count);
    }

    [Fact]
    public void Import_MissingBuildRecord_Fails()
    {
        var sde = SyntheticSde.Create();
        sde.Set("_sde", false, "{\"key\":\"sde\"}");

        Assert.Throws<SdeImportException>(() => new SdeImporter(sde, small).Import(DatabasePath));
    }

    [Fact]
    public void Import_StreamsTheTypesTableInBoundedMemory()
    {
        const int Lines = 400_000;
        const long GrowthLimitBytes = 40L * 1024 * 1024;
        var sde = SyntheticSde.Create();
        var probe = new MemoryProbe();
        sde.SetStream("types", () => new GeneratedLinesStream(Lines, probe));

        new SdeImporter(sde, small).Import(DatabasePath);

        Assert.True(probe.Samples > 3);
        Assert.True(probe.TotalBytes > 2 * GrowthLimitBytes, $"generated only {probe.TotalBytes} bytes");
        Assert.True(probe.MaxGrowth < GrowthLimitBytes, $"live memory grew by {probe.MaxGrowth} bytes");
        Assert.Equal(8, new ReferenceDatabase(DatabasePath).ShipNames("en").Count);
    }

    [Fact]
    public async Task Import_LiveSde_RecordsTimeSizeAndRowCounts()
    {
        var cache = await SdeTestCache.TryGetAsync();
        Assert.SkipUnless(cache is not null, "SDE cache absent; set SAYWHAT_FETCH_TEST_SDE=1 to fetch it.");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = new SdeImporter(new DirectorySdeSource(cache!)).Import(DatabasePath);
        started.Stop();

        Console.WriteLine($"SDE import: {started.Elapsed.TotalSeconds:F1}s, {new FileInfo(DatabasePath).Length} bytes, version {result.SdeVersion}, ships {result.ShipTypes}, ship names {result.ShipNames}, systems {result.SolarSystems}, system names {result.SystemNames}");
        var database = new ReferenceDatabase(DatabasePath);
        Assert.Equal(8, database.ClientLanguages().Count);
        var rifter = database.FindShipType("en", "Rifter");
        Assert.NotNull(rifter);
        foreach (var language in database.ClientLanguages())
        {
            Assert.False(string.IsNullOrEmpty(database.ShipName(rifter.Value, language)));
        }

        var jita = database.FindSystem("en", "Jita");
        Assert.NotNull(jita);
        Assert.Equal("吉他", database.SystemName(jita.Value, "zh"));
    }

    private sealed class FaultingStream(byte[] first) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= first.Length)
            {
                throw new IOException("interrupted");
            }

            var take = Math.Min(count, first.Length - position);
            Array.Copy(first, position, buffer, offset, take);
            position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class MemoryProbe
    {
        private long baseline = -1;

        public int Samples { get; private set; }

        public long TotalBytes { get; set; }

        public long MaxGrowth { get; private set; }

        public void Sample()
        {
            var live = GC.GetTotalMemory(forceFullCollection: true);
            baseline = baseline < 0 ? live : baseline;
            MaxGrowth = Math.Max(MaxGrowth, live - baseline);
            Samples++;
        }
    }

    private sealed class GeneratedLinesStream(int lines, MemoryProbe probe) : Stream
    {
        private const int SampleEvery = 50_000;
        private const string Padding = "{\"mass\":1,\"portionSize\":1,\"published\":true,\"description\":\"filler filler filler filler filler filler filler filler filler filler filler filler\"}";

        private byte[] current = [];
        private int offset;
        private int line;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int start, int count)
        {
            if (offset >= current.Length)
            {
                if (line >= lines)
                {
                    return 0;
                }

                if (line % SampleEvery == 0)
                {
                    probe.Sample();
                }

                current = Encoding.UTF8.GetBytes(NextLine(line++));
                offset = 0;
                probe.TotalBytes += current.Length;
            }

            var take = Math.Min(count, current.Length - offset);
            Array.Copy(current, offset, buffer, start, take);
            offset += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long seekOffset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();

        private static string NextLine(int index)
        {
            var isShip = index % 100_000 < 2;
            return $"{{\"typeID\":{10_000 + index},\"groupID\":{(isShip ? 25 : 99)},\"name\":{SyntheticSde.NameMap(code => "N" + index + code)},{Padding[1..]}\n";
        }
    }
}
