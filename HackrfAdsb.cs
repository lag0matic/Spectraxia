using System.Numerics;
using System.Runtime.InteropServices;

namespace Spectraxia;

// 8 MS/s signed I/Q -> 4 MS/s unsigned I/Q. Keep two timing phases per ADS-B half-bit.
internal sealed class HackrfChannel
{
    private const int Taps = 48;
    private static readonly float[] Kernel = MakeKernel();
    private readonly float[] real = new float[2*Taps], imag = new float[2*Taps];
    private int cursor, phase, pending = -1;
    private byte[] output = [];

    private static float[] MakeKernel()
    {
        double cutoff = 1400000.0 / 8000000;
        var taps = new float[Taps];
        for (int k = 0; k < taps.Length; k++)
        {
            double x = k - (Taps - 1) * .5;
            double window = .42 - .5*Math.Cos(2*Math.PI*k/(Taps-1)) + .08*Math.Cos(4*Math.PI*k/(Taps-1));
            taps[k] = (float)(window * Math.Sin(2*Math.PI*cutoff*x)/(Math.PI*x));
        }
        float sum = taps.Sum();
        for (int k = 0; k < taps.Length; k++) taps[k] /= sum;
        return taps;
    }

    internal ReadOnlySpan<byte> Convert(ReadOnlySpan<byte> input)
    {
        int capacity = input.Length / 2 + 4;
        if (output.Length < capacity) output = new byte[capacity];
        int n = 0, written = 0;
        while (n < input.Length)
        {
            int i = pending >= 0 ? pending : input[n++];
            if (n == input.Length) { pending = i; break; }
            pending = -1;
            real[cursor] = real[cursor+Taps] = unchecked((sbyte)i);
            imag[cursor] = imag[cursor+Taps] = unchecked((sbyte)input[n++]);
            if (++cursor == Taps) cursor = 0;
            if (++phase != 2) continue;
            phase = 0;
            var re = Vector<float>.Zero;
            var im = Vector<float>.Zero;
            int k = 0;
            for (; k <= Taps - Vector<float>.Count; k += Vector<float>.Count)
            {
                var coeff = new Vector<float>(Kernel, k);
                re += coeff * new Vector<float>(real, cursor+k);
                im += coeff * new Vector<float>(imag, cursor+k);
            }
            float a = Vector.Sum(re), b = Vector.Sum(im);
            for (; k < Taps; k++) { a += Kernel[k]*real[cursor+k]; b += Kernel[k]*imag[cursor+k]; }
            output[written++] = (byte)Math.Clamp((int)MathF.Round(a)+128, 0, 255);
            output[written++] = (byte)Math.Clamp((int)MathF.Round(b)+128, 0, 255);
        }
        return output.AsSpan(0,written);
    }
}

internal static class HackrfAdsb
{
    private const string Lib = "hackrf";
    [StructLayout(LayoutKind.Sequential)]
    private struct Transfer
    {
        public IntPtr Device, Buffer;
        public int BufferLength, ValidLength;
        public IntPtr RxContext, TxContext;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Reader(ref Transfer transfer);
    [DllImport(Lib)] private static extern int hackrf_init();
    [DllImport(Lib)] private static extern int hackrf_exit();
    [DllImport(Lib)] private static extern int hackrf_open(out IntPtr device);
    [DllImport(Lib)] private static extern int hackrf_close(IntPtr device);
    [DllImport(Lib)] private static extern int hackrf_set_sample_rate(IntPtr device, double rate);
    [DllImport(Lib)] private static extern int hackrf_set_baseband_filter_bandwidth(IntPtr device, uint hz);
    [DllImport(Lib)] private static extern int hackrf_set_freq(IntPtr device, ulong hz);
    [DllImport(Lib)] private static extern int hackrf_set_lna_gain(IntPtr device, uint db);
    [DllImport(Lib)] private static extern int hackrf_set_vga_gain(IntPtr device, uint db);
    [DllImport(Lib)] private static extern int hackrf_set_amp_enable(IntPtr device, byte enabled);
    [DllImport(Lib)] private static extern int hackrf_set_antenna_enable(IntPtr device, byte enabled);
    [DllImport(Lib)] private static extern int hackrf_start_rx(IntPtr device, Reader read, IntPtr context);
    [DllImport(Lib)] private static extern int hackrf_is_streaming(IntPtr device);
    [DllImport(Lib)] private static extern int hackrf_stop_rx(IntPtr device);
    [DllImport(Lib)] private static extern IntPtr hackrf_error_name(int code);

    private static void Check(int code, string operation)
    {
        if (code == 0) return;
        throw new IOException($"{operation}: {Marshal.PtrToStringAnsi(hackrf_error_name(code))} ({code})");
    }

    internal static void Run(AdsbTraffic traffic, HackrfTuner tuner, CancellationToken stop, Action<int>? receivedBytes = null)
    {
        while (!stop.IsCancellationRequested)
        {
            try { Receive(traffic, tuner, stop, receivedBytes); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { traffic.Status = "HackRF USB library unavailable · check the preview folder"; return; }
            catch (IOException ex) { traffic.Status = ex.Message; }
            if (stop.WaitHandle.WaitOne(1500)) break;
        }
    }

    private static void Receive(AdsbTraffic traffic, HackrfTuner tuner, CancellationToken stop, Action<int>? receivedBytes)
    {
        Check(hackrf_init(), "Initialize HackRF");
        IntPtr device = IntPtr.Zero;
        bool started = false;
        var channel = new HackrfChannel();
        var decoder = new AdsbDecoder(frame => traffic.Update(frame, Environment.TickCount64), samplesPerHalfBit: 2);
        byte[] buffer = [];
        Exception? failure = null;
        long lastData = Environment.TickCount64;
        int Read(ref Transfer transfer)
        {
            try
            {
                if (stop.IsCancellationRequested) return -1;
                int count = transfer.ValidLength;
                if (count < 0 || count > transfer.BufferLength || transfer.Buffer == IntPtr.Zero)
                    throw new InvalidDataException("Invalid HackRF USB transfer");
                if (buffer.Length < count) buffer = new byte[count];
                Marshal.Copy(transfer.Buffer, buffer, 0, count);
                decoder.Feed(channel.Convert(buffer.AsSpan(0,count)));
                if (count > 0)
                {
                    Interlocked.Exchange(ref lastData, Environment.TickCount64);
                    receivedBytes?.Invoke(count);
                }
                return 0;
            }
            catch (Exception ex) { Volatile.Write(ref failure, ex); return -1; }
        }
        Reader callback = Read;
        try
        {
            int result = hackrf_open(out var opened);
            if (result == -5) throw new IOException("Waiting for HackRF · select HackRF mode on the PortaPack");
            Check(result, "Open HackRF");
            device = opened;
            Check(hackrf_set_antenna_enable(device, 0), "Disable HackRF antenna power");
            Check(hackrf_set_amp_enable(device, 0), "Disable HackRF RF amp");
            Check(hackrf_set_sample_rate(device, 8000000), "Set HackRF sample rate");
            Check(hackrf_set_baseband_filter_bandwidth(device, 1750000), "Set HackRF filter");
            Check(hackrf_set_freq(device, AdsbDecoder.Frequency), "Tune HackRF to 1090 MHz");
            HackrfGain? applied = null;
            void Apply()
            {
                var wanted = tuner.Gain;
                if (wanted.Lna != applied?.Lna) Check(hackrf_set_lna_gain(device, (uint)wanted.Lna), "Set HackRF LNA");
                if (wanted.Vga != applied?.Vga) Check(hackrf_set_vga_gain(device, (uint)wanted.Vga), "Set HackRF VGA");
                applied = wanted;
            }
            Apply();
            lastData = Environment.TickCount64;
            Check(hackrf_start_rx(device, callback, IntPtr.Zero), "Start HackRF RX");
            started = true;
            traffic.Status = "HackRF listening · 1090 MHz";
            while (!stop.WaitHandle.WaitOne(60))
            {
                if (Volatile.Read(ref failure) is {} error) throw new IOException(error.Message, error);
                if (hackrf_is_streaming(device) != 1) throw new IOException("HackRF stream ended · reconnecting");
                if (Environment.TickCount64 - Interlocked.Read(ref lastData) > 3000)
                    throw new IOException("HackRF sample stream stalled · reconnecting");
                Apply();
            }
        }
        finally
        {
            if (started) hackrf_stop_rx(device);
            if (device != IntPtr.Zero) hackrf_close(device);
            hackrf_exit();
            GC.KeepAlive(callback);
        }
    }
}
