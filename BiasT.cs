namespace Spectraxia;

internal enum BiasState { Unknown, Off, Enabling, On, Disabling }

internal static class BiasButton
{
    internal static void Update(Button button, BiasState state, bool available = true)
    {
        button.Text = "Bias-T " + state.ToString().ToLowerInvariant();
        button.ForeColor = state switch
        {
            BiasState.On => Color.Orange,
            BiasState.Off => Color.LightGreen,
            _ => Color.Khaki
        };
        button.Enabled = available && state is (BiasState.On or BiasState.Off);
    }
}

// Each open receiver gets a fresh instance. Requests cannot cross device sessions.
internal sealed class BiasT
{
    private int state;
    internal BiasState State => (BiasState)Volatile.Read(ref state);
    internal void Toggle()
    {
        var current = State;
        if (current is BiasState.Off or BiasState.On)
            Interlocked.CompareExchange(ref state, (int)(current == BiasState.Off ? BiasState.Enabling : BiasState.Disabling), (int)current);
    }
    internal void Open(Func<bool,int> set)
    {
        Radio.Check(set(false), "Disable bias-T");
        Volatile.Write(ref state, (int)BiasState.Off);
    }
    internal void Apply(Func<bool,int> set)
    {
        var current = State;
        if (current is not (BiasState.Enabling or BiasState.Disabling)) return;
        bool on = current == BiasState.Enabling;
        Volatile.Write(ref state, (int)BiasState.Unknown);
        Radio.Check(set(on), on ? "Enable bias-T" : "Disable bias-T");
        Volatile.Write(ref state, (int)(on ? BiasState.On : BiasState.Off));
    }
    internal void Close(Func<bool,int> set)
    {
        Volatile.Write(ref state, (int)BiasState.Unknown);
        Radio.Check(set(false), "Bias-T shutdown");
    }
}
