namespace Spectraxia;

// Integrate complex 57 kHz baseband in sixteenths of a bit. Differential decisions
// cancel an arbitrary subcarrier phase. Parallel timing candidates need no pilot lock.
internal sealed class Rds
{
    private readonly Lane[] lanes = Enumerable.Range(0,16).Select(_ => new Lane()).ToArray();
    private readonly double[] iRing = new double[16], qRing = new double[16];
    private double iSum, qSum;
    private int clock, slot;
    internal string Name { get; private set; } = "";
    internal string Text { get; private set; } = "";
    private int pi = -1, textId = -1, nameMask, textMask;
    private readonly char[] name = new char[8], text = new char[64];
    internal int TextGroups { get; private set; }

    internal void Sample(double i, double q)
    {
        iSum += i; qSum += q;
        clock += 19000;
        if (clock < Radio.Rate) return;
        clock -= Radio.Rate;
        iRing[slot] = iSum; qRing[slot] = qSum;
        iSum = qSum = 0;
        double bi = 0, bq = 0;
        for (int n = 0; n < 8; n++)
        {
            bi += iRing[(slot-n)&15] - iRing[(slot-n-8)&15];
            bq += qRing[(slot-n)&15] - qRing[(slot-n-8)&15];
        }
        var lane = lanes[slot];
        if (lane.Push(bi,bq)) Group(lane.Words);
        slot = (slot+1)&15;
    }
    internal static int Remainder(uint word)
    {
        for (int bit = 25; bit >= 10; bit--)
            if ((word & (1u<<bit)) != 0) word ^= 0x5B9u << (bit-10);
        return (int)word;
    }
    private sealed class Lane
    {
        private double previousI, previousQ;
        private uint a,b,c,d;
        private int received;
        internal readonly int[] Words = new int[4];
        internal bool Push(double i, double q)
        {
            uint bit = i*previousI + q*previousQ < 0 ? 1u : 0u;
            previousI = i; previousQ = q;
            const uint mask = 0x3ffffff;
            a = ((a<<1) | (b>>25)) & mask;
            b = ((b<<1) | (c>>25)) & mask;
            c = ((c<<1) | (d>>25)) & mask;
            d = ((d<<1) | bit) & mask;
            if (received < 104) received++;
            if (received < 104 || Remainder(d) != 0x1b4 || Remainder(a) != 0xfc ||
                Remainder(b) != 0x198 || Remainder(c) != ((b & (0x800u<<10)) == 0 ? 0x168 : 0x350)) return false;
            Words[0]=(int)(a>>10); Words[1]=(int)(b>>10);
            Words[2]=(int)(c>>10); Words[3]=(int)(d>>10);
            return true;
        }
    }
    private static char Character(int code) => (code & 255) switch
    {
        13 => '\r', >= 32 and <= 126 => (char)(code & 255), _ => ' '
    };
    private static void Pair(char[] output, int at, int word)
    {
        output[at] = Character(word>>8); output[at+1] = Character(word);
    }
    private void Group(int[] w)
    {

        if (pi != w[0])
        {
            pi = w[0]; Name = Text = ""; nameMask = textMask = 0; textId = -1;
        }
        int b = w[1], type = b>>12;
        if (type == 0)
        {
            int part = b&3;
            Pair(name,part*2,w[3]); nameMask |= 1<<part;
            if (nameMask == 15) { Name = new string(name).Trim(); nameMask = 0; }
        }
        if (type != 2) return;
        TextGroups++;
        int id = b & 0x810;
        if (id != textId) { textId = id; textMask = 0; Text = ""; Array.Clear(text); }
        int segment = b&15, stride = (b&0x800)==0 ? 4 : 2;
        int pos = segment*stride;
        if (stride == 4) Pair(text,pos,w[2]);
        Pair(text,pos+stride-2,w[3]); textMask |= 1<<segment;
        int contiguous = 0;
        while (contiguous < 16 && (textMask & (1<<contiguous)) != 0) contiguous++;
        int end = Array.IndexOf(text,'\r',0,contiguous*stride);
        if (end >= 0 || contiguous == 16) Text = new string(text,0,end >= 0 ? end : 16*stride).Trim();
    }
}
