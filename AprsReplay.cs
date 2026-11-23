namespace Spectraxia;

internal static class AprsReplay
{
    internal static void Read(string path,CaptureBand? raw,Action<DateTimeOffset,AprsPacket> receive,CancellationToken stop)
    {
        var source=new RecordingSource(path,raw);
        DateTimeOffset timestamp=default;
        var modem=new AprsModem(p=>receive(timestamp,p));
        ReceiverAudio? radio=null;
        CaptureBand? previous=null;
        var settings=new ReceiverSettings(Mode:ListenMode.NFM,FilterHz:AprsWindow.Dial.Width);
        for(int page=0;page<source.Pages;page++)
        foreach(var block in source.Page(page,stop))
        {
            stop.ThrowIfCancellationRequested();
            var band=block.Band;
            timestamp=block.Utc;
            if(previous==null || previous.Session!=band.Session || previous.Rate!=band.Rate ||
                previous.Center!=band.Center || previous.Settled!=band.Settled ||
                (block.Iq.Length==0 && previous.Station!=band.Station))
            {
                modem=new AprsModem(p=>receive(timestamp,p));
                radio=null;
            }
            previous=band;
            if(!band.Settled) continue;
            if(block.Iq.Length!=0)
            {
                long hz=AprsWindow.Dial.Hz;
                if(!TuningView.Fits(hz,band.Center,band.Rate,settings.Mode,settings.Width)) continue;
                radio??=new ReceiverAudio(band.Rate);
                var pcm=radio.Decode(block.Iq,band.Center,hz,settings,100);
                modem.Feed(pcm);
            }
            else if(block.Channels==1) modem.Feed(block.Pcm);
            else
            {
                var mono=new short[block.Pcm.Length/2];
                for(int i=0;i<mono.Length;i++) mono[i]=(short)((block.Pcm[2*i]+block.Pcm[2*i+1])/2);
                modem.Feed(mono);
            }
        }
    }
    internal static int Run(string[] args)
    {
        try
        {
            CaptureBand? raw=null;
            if(args.Length==4) raw=new(int.Parse(args[2]),long.Parse(args[3]),AprsWindow.Dial.Hz,ListenMode.NFM,15000,true,Guid.Empty);
            int count=0;
            Read(args[1],raw,(time,p)=>
            {
                count++;
                Console.WriteLine($"{time:O}  {p.Source}>{p.Destination}  {p.Path}  {p.Kind}  {p.Position}  {p.Summary}");
            },CancellationToken.None);
            Console.WriteLine($"{count} CRC-valid AX.25 UI packets");
            return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex.Message);return 1;}
    }
}
