using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;
namespace Wagenheimer.MsxAudio.Tests;

public sealed class DirectExtractionTests
{
    [Fact]
    public void LoopDetectionRejectsRepeatedNotesWithDifferentTiming()
    {
        var events = new List<VgmEvent>();
        long sample=0;
        for(int pass=0;pass<3;pass++)
            for(int note=0;note<120;note++)
            {
                events.Add(new VgmEvent(sample,VgmEventKind.Psg,0,(byte)note,0));
                sample += 4410*(pass+1);
            }
        Assert.Null(VgmLoopFinder.Find(VgmFile.FromEvents(events,(int)sample,1789772.5,"timing"),3));
    }

    [Fact]
    public void DriverOnlyCaptureIgnoresGameHooksAndStartsEachIdIndependently()
    {
        string folder = Path.Combine(Path.GetTempPath(), "driver-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var rom = new byte[0x8000]; rom[0]=65;rom[1]=66;rom[2]=0x20;rom[3]=0x40;
            // INIT installs a game hook which would overwrite PSG register 0 with 99.
            byte[] init = {0x3E,0xC3,0x32,0x9F,0xFD,0x21,0x00,0x41,0x22,0xA0,0xFD,0xC9};
            init.CopyTo(rom,0x20);
            byte[] hook = {0x3E,99,0xCD,0x40,0x40,0xC9}; hook.CopyTo(rom,0x100);
            // Sound call: select period A, mixer and volume.
            byte[] sound = {0x5F,0x3E,0,0xD3,0xA0,0x7B,0xD3,0xA1,0x3E,7,0xD3,0xA0,0x3E,0x3E,0xD3,0xA1,0x3E,8,0xD3,0xA0,0x3E,15,0xD3,0xA1,0xC9};
            sound.CopyTo(rom,0x40); rom[0x200]=0xC9; // isolated driver tick
            string path=Path.Combine(folder,"test.rom"); File.WriteAllBytes(path,rom);
            var profile = new RomExtractionProfile { SoundCallAddress=0x4040, PlayerAddress=0x4200, StopId=0, Ids=new(){7,8}, SecondsPerId=0.1 };
            using var forward=DirectRomExtractor.Extract(new(){RomPath=path,Profile=profile}).Library;
            profile.Ids.Reverse();
            using var reverse=DirectRomExtractor.Extract(new(){RomPath=path,Profile=profile}).Library;
            var v=forward.LoadVgm(forward.FindByOriginalId(7)!);
            Assert.DoesNotContain(v.Events,e=>e.Kind==VgmEventKind.Psg&&e.P0==0&&e.P1==99);
            Assert.Equal(VgmWriter.Write(v),VgmWriter.Write(reverse.LoadVgm(reverse.FindByOriginalId(7)!)));

            // An opt-in state contract must end the capture when the declared activity byte clears.
            profile.DriverObservation = new() { ActiveAddresses = new() { 0xE000 }, Required = true };
            profile.SecondsPerId = 1;
            using var completed = DirectRomExtractor.Extract(new() { RomPath = path, Profile = profile }).Library;
            Assert.True(completed.FindByOriginalId(7)!.Seconds < 0.5);
        }
        finally { Directory.Delete(folder,true); }
    }

    [Fact]
    public void AlreadyCancelledExtractionDoesNotBootOrPublishAnything()
    {
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>DirectRomExtractor.Extract(new(), cancellationToken:cts.Token));
    }
}
