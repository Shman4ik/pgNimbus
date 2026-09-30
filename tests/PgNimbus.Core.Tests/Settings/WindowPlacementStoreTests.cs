using PgNimbus.Core.Settings;

namespace PgNimbus.Core.Tests.Settings;

public class WindowPlacementStoreTests
{
    [Test]
    public async Task Load_NoFile_ReturnsNull()
    {
        var store = new WindowPlacementStore(Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json"));

        var placement = store.Load();

        await Assert.That(placement).IsNull();
    }

    [Test]
    public async Task Load_CorruptFile_ReturnsNullAndDoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "not valid json {{{");

        try
        {
            var placement = new WindowPlacementStore(path).Load();

            await Assert.That(placement).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SaveThenLoad_RoundTripsPlacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new WindowPlacementStore(path);

            store.Save(new WindowPlacement(X: -120, Y: 42, Width: 1280.5, Height: 900, IsMaximized: true));

            var placement = store.Load();

            await Assert.That(placement).IsNotNull();
            await Assert.That(placement!.X).IsEqualTo(-120);
            await Assert.That(placement.Y).IsEqualTo(42);
            await Assert.That(placement.Width).IsEqualTo(1280.5);
            await Assert.That(placement.Height).IsEqualTo(900);
            await Assert.That(placement.IsMaximized).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Save_Twice_KeepsOnlyTheLatest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pgnimbus-{Guid.NewGuid():N}.json");

        try
        {
            var store = new WindowPlacementStore(path);
            store.Save(new WindowPlacement(0, 0, 1100, 800, IsMaximized: false));
            store.Save(new WindowPlacement(200, 100, 1440, 960, IsMaximized: false));

            var placement = store.Load();

            await Assert.That(placement).IsNotNull();
            await Assert.That(placement!.X).IsEqualTo(200);
            await Assert.That(placement.Y).IsEqualTo(100);
            await Assert.That(placement.Width).IsEqualTo(1440);
            await Assert.That(placement.Height).IsEqualTo(960);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
