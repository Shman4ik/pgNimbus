namespace PgNimbus.Core.Tests;

public class DurationTextTests
{
    [Test]
    [Arguments(0d, "0.00 ms")]
    [Arguments(0.042, "0.04 ms")]
    [Arguments(4.25, "4.3 ms")]
    [Arguments(12.4, "12 ms")]
    [Arguments(999.4, "999 ms")]
    [Arguments(1500d, "1.5 s")]
    [Arguments(59_949d, "59.9 s")]
    [Arguments(90_000d, "1.5 min")]
    [Arguments(7_200_000d, "2.0 h")]
    [Arguments(-5d, "0.00 ms")]
    public async Task Picks_the_unit_per_value(double milliseconds, string expected) =>
        await Assert.That(DurationText.Format(milliseconds)).IsEqualTo(expected);
}
