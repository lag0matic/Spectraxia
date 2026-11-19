using System.Runtime.InteropServices;

namespace Spectraxia;

internal static class Radio
{
    internal const int Rate = 240000;
    internal const int CaptureRate = 4 * Rate;
    internal static readonly int[] SampleRates = [CaptureRate, 1_920_000, 2_400_000];
    private const string Lib = "rtlsdr";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Reader(IntPtr bytes, uint length, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern uint rtlsdr_get_device_count();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_open(out IntPtr device, uint index);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_close(IntPtr device);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_set_bias_tee(IntPtr device, int on);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_set_sample_rate(IntPtr device, uint rate);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern uint rtlsdr_get_sample_rate(IntPtr device);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_set_center_freq(IntPtr device, uint hz);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_set_tuner_gain_mode(IntPtr device, int manual);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_get_tuner_gains(IntPtr device, [Out] int[]? gains);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_set_tuner_gain(IntPtr device, int tenths);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_set_agc_mode(IntPtr device, int enabled);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_reset_buffer(IntPtr device);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_read_async(IntPtr device, Reader read, IntPtr context, uint buffers, uint bytes);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int rtlsdr_cancel_async(IntPtr device);

    internal static void Check(int code, string operation)
    {
        if (code != 0) throw new IOException($"{operation} failed ({code})");
    }

    internal static void Receive(long frequency, ReceiverSettings settings, Func<ReceiverSettings> latestSettings,
        Func<long> requestedCenter, Action<int[]> reportSteps, CancellationToken stop, Action<byte[], long, bool> consume,
        Action? onRetuned = null, BiasT? bias = null)
    {
        if (rtlsdr_get_device_count() == 0) throw new IOException("Waiting for USB receiver");
        Check(rtlsdr_open(out var device, 0), "Open receiver");
        bias ??= new BiasT();
        Func<bool,int> setBias = on => rtlsdr_set_bias_tee(device, on ? 1 : 0);
        try
        {
            bias.Open(setBias);
            Check(rtlsdr_set_sample_rate(device, (uint)settings.SampleRate), "Set sample rate");
            if (Math.Abs((long)rtlsdr_get_sample_rate(device) - settings.SampleRate) > 100)
                throw new IOException("Receiver did not apply the selected capture width");
            Check(rtlsdr_set_center_freq(device, checked((uint)frequency)), "Tune receiver");
            long appliedCenter = frequency;
            // Already-submitted USB buffers may straddle an LO change. Keep audio flowing,
            // but wait one transfer queue before attributing new FFT rows to the new center.
            const int buffers = 8;
            int uncertainBuffers = 0;
            long tuneGeneration = 0;
            int count = rtlsdr_get_tuner_gains(device, null);
            var steps = count is > 0 and < 4096 ? new int[count] : Array.Empty<int>();
            if (steps.Length != 0 && rtlsdr_get_tuner_gains(device, steps) != count)
                throw new IOException("Could not read tuner gain steps");
            Array.Sort(steps);
            reportSteps(steps);
            ReceiverSettings? applied = null;
            void ApplyGain(ReceiverSettings next)
            {
                bool manual = next.Manual && steps.Length > 0;
                bool modeChanged = applied == null || next.Manual != applied.Manual;
                if (modeChanged)
                    Check(rtlsdr_set_tuner_gain_mode(device, manual ? 1 : 0), "Set tuner gain mode");
                if (manual && (modeChanged || next.GainTenths != applied!.GainTenths))
                    Check(rtlsdr_set_tuner_gain(device, next.NearestGain(steps)), "Set manual tuner gain");
                if (applied == null || next.DigitalAgc != applied.DigitalAgc)
                    Check(rtlsdr_set_agc_mode(device, next.DigitalAgc ? 1 : 0), "Set RTL digital AGC");
                applied = next;
            }
            ApplyGain(settings);
            Check(rtlsdr_reset_buffer(device), "Reset USB buffer");
            byte[] buffer = Array.Empty<byte>();
            Exception? failure = null;
            Reader read = (data, size, _) =>
            {
                if (stop.IsCancellationRequested || failure != null) return;
                try
                {
                    long generation = Interlocked.Read(ref tuneGeneration);
                    long center = Interlocked.Read(ref appliedCenter);
                    if (buffer.Length != size) buffer = new byte[size];
                    Marshal.Copy(data, buffer, 0, buffer.Length);
                    bool settled = (generation & 1) == 0 && Interlocked.Read(ref tuneGeneration) == generation;
                    if (Volatile.Read(ref uncertainBuffers) > 0)
                    {
                        Interlocked.Decrement(ref uncertainBuffers);
                        settled = false;
                    }
                    consume(buffer, center, settled);
                }
                catch (Exception ex) { failure = ex; rtlsdr_cancel_async(device); }
            };
            // USB control transfers run outside the libusb sample callback. Join before closing the handle.
            using var controls = new CancellationTokenSource();
            var worker = Task.Run(async () =>
            {
                while (!controls.IsCancellationRequested)
                {
                    if (stop.IsCancellationRequested || Volatile.Read(ref failure) != null)
                        rtlsdr_cancel_async(device);
                    else
                    {
                        try
                        {
                            ApplyGain(latestSettings());
                            bias.Apply(setBias);
                            long desired = requestedCenter();
                            if (desired != Interlocked.Read(ref appliedCenter))
                            {
                                Interlocked.Increment(ref tuneGeneration);
                                Check(rtlsdr_set_center_freq(device, checked((uint)desired)), "Retune receiver");
                                onRetuned?.Invoke();
                                Interlocked.Exchange(ref appliedCenter, desired);
                                Interlocked.Exchange(ref uncertainBuffers, buffers);
                                Interlocked.Increment(ref tuneGeneration);
                            }
                        }
                        catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                    }
                    try { await Task.Delay(40, controls.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            });
            int result;
            try { result = rtlsdr_read_async(device, read, IntPtr.Zero, buffers, 32768); }
            finally { controls.Cancel(); worker.GetAwaiter().GetResult(); }
            if (failure != null) throw failure;
            if (!stop.IsCancellationRequested) Check(result == 0 ? -1 : result, "USB capture ended");
        }
        finally
        {
            try { bias.Close(setBias); }
            finally { rtlsdr_close(device); }
        }
    }
}

internal sealed class Wfm(int initialPhase = 0)
{
    // Mono baseband low-pass before 5:1 decimation, followed by US 75 us de-emphasis.
    private const int Taps = 81;
    private readonly double[] history = new double[Taps];
    private readonly double[] difference = new double[Taps];
    internal readonly Rds Rds = new();
    internal bool StereoLocked { get; private set; }
    private static readonly double[] Sin = Enumerable.Range(0,240).Select(n => Math.Sin(2*Math.PI*n/240)).ToArray();
    private int carrier, pilotSamples, confidence;
    private double pilotI, pilotQ, lastAngle, differenceGain, differenceDeemphasis;
    private double rotateSin, rotateCos = 1;
    private static readonly double[] Filter = MakeFilter();
    private double previousI, previousQ, deemphasis;
    private int position, phase = initialPhase;
    private static double[] MakeFilter()
    {
        var taps = new double[Taps];
        for (int j = 0; j < Taps; j++)
        {
            int k = j - Taps / 2;
            double cutoff = 15_000.0 / Radio.Rate;
            taps[j] = (k == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * k) / (Math.PI * k))
                * (0.54 - 0.46 * Math.Cos(2 * Math.PI * j / (Taps - 1)));
        }
        double sum = taps.Sum();
        return taps.Select(x => x / sum).ToArray();
    }
    internal short[] Decode(byte[] iq, bool twoChannels = false, bool mono = false)
    {
        var pcm = new short[((iq.Length / 2 + phase) / 5) * (twoChannels ? 2 : 1)];
        int written = 0;
        for (int n = 0; n + 1 < iq.Length; n += 2)
        {
            double i = iq[n] - 127.5, q = iq[n + 1] - 127.5;
            double composite = Math.Atan2(previousI*q - previousQ*i, previousI*i + previousQ*q);
            history[position] = composite;
            double s = Sin[carrier], c = Sin[(carrier+60)%240];
            pilotI += composite*s; pilotQ += composite*c;
            int twice = (2*carrier)%240, triple = (3*carrier)%240;
            difference[position] = 2*composite*(Sin[twice]*rotateCos + Sin[(twice+60)%240]*rotateSin);
            Rds.Sample(composite*Sin[triple], composite*Sin[(triple+60)%240]);
            carrier = (carrier+19)%240;
            if (++pilotSamples == 240)
            {
                double angle = Math.Atan2(pilotQ,pilotI);
                double drift = Math.IEEERemainder(angle-lastAngle,2*Math.PI);
                bool coherent = pilotI*pilotI+pilotQ*pilotQ > 24 && Math.Abs(drift) < .24;
                confidence = Math.Clamp(confidence + (coherent ? 1 : -2),0,30);
                if (confidence == 30) StereoLocked = true;
                if (confidence < 8) StereoLocked = false;
                (rotateSin,rotateCos) = Math.SinCos(2*(angle + drift));
                pilotI = pilotQ = 0; pilotSamples = 0; lastAngle = angle;
            }
            previousI = i;
            previousQ = q;
            position = (position + 1) % Taps;
            if (++phase < 5) continue;
            phase = 0;
            double signal = 0, diff = 0;
            for (int j = 0; j < Taps; j++)
            {
                int index = (position+j)%Taps;
                signal += Filter[j]*history[index];
                diff += Filter[j]*difference[index];
            }
            deemphasis += 0.242535 * (signal - deemphasis);
            differenceDeemphasis += .242535*(diff-differenceDeemphasis);
            differenceGain += .001*((StereoLocked && !mono ? 1 : 0)-differenceGain);
            double side = twoChannels ? differenceGain*differenceDeemphasis : 0;
            pcm[written++] = (short)Math.Clamp((deemphasis+side)*15000,short.MinValue,short.MaxValue);
            if (twoChannels) pcm[written++] = (short)Math.Clamp((deemphasis-side)*15000,short.MinValue,short.MaxValue);
        }
        return pcm;
    }
}
