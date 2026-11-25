using Wagenheimer.MsxAudio.Extraction;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public sealed class DriverObservationTests
{
    [Fact]
    public void CompletionRequiresEveryConfiguredChannelToStop()
    {
        byte[] ram = new byte[65536]; ram[0xE000] = 19;
        var observer = new DriverStateObserver(new() { ActiveAddresses = new() { 0xE000, 0xE033 } });
        Assert.False(observer.Observe(a => ram[a], 735).Finished);
        ram[0xE000] = 0;
        Assert.True(observer.Observe(a => ram[a], 1470).Finished);
    }

    [Fact]
    public void LoopRequiresTwoEqualPeriodsAndIncludesWaitCounters()
    {
        byte[] ram = new byte[65536]; ram[0xE000] = 5;
        var observer = new DriverStateObserver(new()
        {
            ActiveAddresses = new() { 0xE000 },
            StateRanges = new() { new() { Address = 0xE000, Length = 5 } }
        });
        Assert.Null(observer.Observe(a => ram[a], 0).Loop);
        ram[0xE004] = 1; // same pointers, a different wait counter is not the same state
        Assert.Null(observer.Observe(a => ram[a], 735).Loop);
        ram[0xE004] = 0;
        Assert.Null(observer.Observe(a => ram[a], 1470).Loop);
        ram[0xE004] = 1;
        Assert.Null(observer.Observe(a => ram[a], 2205).Loop);
        ram[0xE004] = 0;
        var loop = observer.Observe(a => ram[a], 2940).Loop;
        Assert.Equal(new DriverLoop(0, 1470), loop);
    }

    [Fact]
    public void InvalidMemoryRangesFailBeforeReadingTheMachine()
    {
        Assert.Throws<InvalidDataException>(() => new DriverStateObserver(new()
        { StateRanges = new() { new() { Address = 65535, Length = 2 } } }));
    }
}
