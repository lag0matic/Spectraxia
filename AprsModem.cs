using System.Text;

namespace Spectraxia;

// 48 kHz mono discriminator audio; Bell 202 at 1200 symbols/s.
internal sealed class AprsModem(Action<AprsPacket> receive)
{
    private const int Symbol = 40, Cycle = 240;
    private static double[] Oscillator(int frequency, double phase) => Enumerable.Range(0,Cycle)
        .Select(n => Math.Cos(2*Math.PI*frequency*n/48000+phase)).ToArray();
    private static readonly double[][] Tone = [Oscillator(1200,0),Oscillator(1200,Math.PI/2),
        Oscillator(2200,0),Oscillator(2200,Math.PI/2)];
    private readonly double[] sums = new double[4];
    private readonly short[] history = new short[Symbol];
    private readonly FrameReader[] phases = Enumerable.Range(0,8).Select(_ => new FrameReader()).ToArray();
    private int phase, slot;
    private long samples, lastEnd = -10000;
    private byte[] last = [];

    internal void Feed(ReadOnlySpan<short> pcm)
    {
        foreach (short sample in pcm)
        {
            int old = history[slot], oldPhase = (phase+Cycle-Symbol)%Cycle;
            history[slot] = sample;
            for (int i=0;i<4;i++) sums[i] += sample*Tone[i][phase]-old*Tone[i][oldPhase];
            if (slot%5 == 0)
            {
                bool mark = sums[0]*sums[0]+sums[1]*sums[1] > sums[2]*sums[2]+sums[3]*sums[3];
                if (phases[slot/5].Symbol(mark) is {} bytes &&
                    (samples-lastEnd > Symbol*3 || !bytes.AsSpan().SequenceEqual(last)))
                {
                    lastEnd = samples;
                    last = bytes;
                    if (AprsPacket.Parse(bytes) is {} packet) receive(packet);
                }
            }
            samples++;
            slot = (slot+1)%Symbol;
            phase = (phase+1)%Cycle;
        }
    }
    private sealed class FrameReader
    {
        private bool prior, collecting;
        private byte shift;
        private readonly byte[] raw = new byte[4096];
        private int bits;
        internal byte[]? Symbol(bool mark)
        {
            byte bit = (byte)(mark == prior ? 1 : 0);
            prior = mark;
            shift = (byte)((shift<<1)|bit);
            if (collecting && bits < raw.Length) raw[bits++] = bit;
            else collecting = false;
            if (shift != 0x7e) return null;
            byte[]? frame = collecting && bits > 8 ? Decode(bits-8) : null;
            bits = 0;
            collecting = true;
            return frame;
        }
        private byte[]? Decode(int count)
        {
            if (count < 18*8) return null;
            Span<byte> frame = stackalloc byte[512];
            frame.Clear();
            int ones = 0, offset = 0;
            for (int i=0;i<count;i++)
            {
                byte bit = raw[i];
                if (ones == 5)
                {
                    if (bit != 0) return null;
                    ones = 0;
                    continue;
                }
                ones = bit == 0 ? 0 : ones+1;
                frame[offset/8] |= (byte)(bit<<(offset%8));
                offset++;
            }
            if (offset%8 != 0 || offset < 18*8) return null;
            var bytes = frame[..(offset/8)];
            ushort crc = 0xffff;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int j=0;j<8;j++) crc=(ushort)((crc>>1)^((crc&1)==0 ? 0 : 0x8408));
            }
            return crc == 0xf0b8 ? bytes[..^2].ToArray() : null;
        }
    }
}
