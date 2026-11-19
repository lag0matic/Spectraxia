namespace Spectraxia;

internal static class BroadcastCheck
{
    // Transmit a known multiplex through FM modulation, including differentially
    // encoded biphase data and independent tones on the left and right channels.
    internal static bool Run()
    {
        var bits = new List<int>();
        void Block(int data, int offset)
        {
            int crc = 0;
            for (int n = 15; n >= 0; n--)
            {
                int feedback = ((crc>>9)^(data>>n))&1;
                crc = ((crc<<1)&1023) ^ (feedback == 0 ? 0 : 0x1b9);
            }
            int encoded = (data<<10) | (crc^offset);
            for (int n=25; n>=0; n--) bits.Add((encoded>>n)&1);
        }
        void Group(int b, int c, int d)
        {
            Block(0x42a1,0xfc); Block(b,0x198); Block(c,0x168); Block(d,0x1b4);
        }
        const string name = "TEST FM ";
        const string text = "Radio works!\r   ";
        int Pair(string s,int i) => s[i]*256+s[i+1];
        for (int i=0;i<4;i++) Group(i,0,Pair(name,2*i));
        for (int i=0;i<4;i++) Group(0x2000+i,Pair(text,4*i),Pair(text,4*i+2));

        var decoder = new Wfm();
        int clock=0, symbol=0, polarity=1, sample=0;
        double fmPhase=0;
        var data = new byte[4096];
        double[] corr = new double[8];
        var differenceEnergy = new double[4];
        for (int stage=0;stage<4;stage++)
        {
            // A full transmission in forced mono verifies independent RBDS reception.
            int length = stage == 1 ? 180 : 96;
            for (int block=0;block<length;block++)
            {
                for (int j=0;j<data.Length;j+=2,sample++)
                {
                    double t = 2*Math.PI*sample/Radio.Rate;
                    double l = .33*Math.Sin(1000*t), r = .27*Math.Sin(2000*t);
                    double p = 19000*t+.63;
                    double m = l+r;
                    if (stage != 3) m += (l-r)*Math.Sin(2*p) + .15*Math.Sin(p);
                    if (stage == 1) m += .09*polarity*(clock<Radio.Rate ? 1:-1)*Math.Cos(3*p+.71);
                    clock += 2375;
                    if (clock >= 2*Radio.Rate)
                    {
                        clock -= 2*Radio.Rate;
                        symbol=(symbol+1)%bits.Count;
                        if (bits[symbol]!=0) polarity=-polarity;
                    }
                    fmPhase += 1.4*m;
                    data[j]=(byte)(127.5+110*Math.Cos(fmPhase));
                    data[j+1]=(byte)(127.5+110*Math.Sin(fmPhase));
                }
                int before=sample-data.Length/2;
                var pcm=decoder.Decode(data,true,stage==1);
                if (block<32) continue;
                for (int j=0;j<pcm.Length;j+=2)
                {
                    double t = 2*Math.PI*(before/5+j/2)/48000.0;
                    differenceEnergy[stage]+=Math.Abs(pcm[j]-pcm[j+1]);
                    if (stage!=0) continue;
                    for(int ch=0;ch<2;ch++)
                    for(int tone=0;tone<2;tone++)
                    {
                        int at=4*ch+2*tone;
                        double angle=(tone+1)*1000*t;
                        corr[at]+=pcm[j+ch]*Math.Cos(angle);
                        corr[at+1]+=pcm[j+ch]*Math.Sin(angle);
                    }
                }
            }
            if (stage == 1 && (decoder.Rds.Name != "TEST FM" || decoder.Rds.Text != "Radio works!")) return false;
        }
        double Power(int at) => corr[at]*corr[at]+corr[at+1]*corr[at+1];
        return Power(0)>100*Power(2) && Power(6)>100*Power(4) && differenceEnergy[0]>100000 &&
            differenceEnergy[1]<100 && differenceEnergy[2]>100000 && differenceEnergy[3]<100 && !decoder.StereoLocked;
    }
}
