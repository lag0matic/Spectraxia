namespace Spectraxia;

internal static class AprsCheck
{
    internal static bool Run()
    {
        var packets=new List<AprsPacket>();
        var modem=new AprsModem(packets.Add);
        // AX.25 UI frame from N0AAA-9 to APTEST. Exercise arbitrary audio block boundaries.
        var bytes=new List<byte>();
        void Address(string call,int ssid,bool last)
        {
            foreach(char c in call.PadRight(6)) bytes.Add((byte)(c<<1));
            bytes.Add((byte)(0x60 | (ssid<<1) | (last?1:0)));
        }
        Address("APTEST",0,false); Address("N0AAA",9,true);
        bytes.AddRange([0x03,0xf0]);
        bytes.AddRange("!3949.00N/08454.00W>Mobile"u8.ToArray());
        ushort fcs=0xffff;
        foreach(byte b in bytes)
        {
            fcs^=b;
            for(int i=0;i<8;i++) fcs=(ushort)((fcs>>1)^((fcs&1)!=0 ? 0x8408 : 0));
        }
        bytes.Add((byte)~fcs);bytes.Add((byte)(~fcs>>8));
        List<short> samples=[];
        double angle=.17;
        bool mark=true;
        void Bit(bool one)
        {
            if(!one) mark=!mark;
            for(int i=0;i<40;i++)
            {
                samples.Add((short)(10000*Math.Sin(angle)));
                angle+=2*Math.PI*(mark?1200:2200)/48000;
            }
        }
        void Flags()
        {
            for(int n=0;n<5;n++) for(int i=0;i<8;i++) Bit(((0x7e>>i)&1)!=0);
        }
        void Frame()
        {
            Flags();
            int ones=0;
            foreach(byte b in bytes)
            for(int i=0;i<8;i++)
            {
                bool one=((b>>i)&1)!=0;
                Bit(one); ones=one?ones+1:0;
                if(ones==5){Bit(false);ones=0;}
            }
            Flags();
        }
        Frame();
        bytes[^1]^=1;
        Frame();
        foreach(var chunk in samples.Chunk(173)) modem.Feed(chunk);
        if(packets.Count!=1 || packets[0] is not { Source:"N0AAA-9", Kind:"Position", Summary:"Mobile", Position:{} position }
            || Math.Abs(position.Lat-39.8166667)>.00001 || Math.Abs(position.Lon+84.9)>.00001) return false;
        var weather=AprsPacket.Interpret("WX","APRS","","_12000000c180s005g010t065h70b10132");
        var msg=AprsPacket.Interpret("CALL","APRS","",":N0AAA-9  :Test");
        var malformed=AprsPacket.Interpret("CALL","APRS","","!9999.99N/18499.99W>bad");
        using var map=new AprsMap { Size=new Size(480,260) };
        map.Remember(packets[0]);
        using var bitmap=new Bitmap(map.Width,map.Height);
        map.DrawToBitmap(bitmap,map.ClientRectangle);
        return bitmap.GetPixel(240,130).ToArgb()==Color.MediumSpringGreen.ToArgb() &&
            weather.Kind=="Weather" && weather.Summary.Contains("65 °F") && msg.Summary=="To N0AAA-9 · Test" && malformed.Position==null;
    }
}
