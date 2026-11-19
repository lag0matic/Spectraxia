namespace Spectraxia;

internal sealed class CapturePanel : FlowLayoutPanel
{
    private readonly CaptureTape tape;
    private readonly ReviewPane review;
    private readonly ComboBox tracks = new() { AccessibleName = "Recording tracks", DropDownStyle = ComboBoxStyle.DropDownList, Width = 135 };
    private readonly CheckBox lead = new() { Text = "Include last 30 s", AutoSize = true };
    private readonly Button record = Button("Record"), save = Button("Save last 30 s"), folderButton = Button("Folder…");
    private readonly Label note = new() { AutoSize = true, MaximumSize = new Size(680, 0) };
    private string folder;
    private Task? recording, export;
    private bool stopping;
    internal event Action<bool>? RecordingChanged;
    private static Button Button(string name) => new() { Text = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    internal CapturePanel(CaptureTape tape, ReviewPane review, string? savedFolder, Action<string> rememberFolder)
    {
        this.tape = tape; this.review = review;
        folder = AppPaths.Recordings(savedFolder);
        Dock = DockStyle.Top; AutoSize = true; Visible = false; Padding = new Padding(4);
        tracks.Items.AddRange(["Audio WAV", "Wideband I/Q", "Audio + I/Q"]); tracks.SelectedIndex = 0;
        var rewind = Button("Rewind");
        var open = Button("Open recording…");
        Controls.AddRange([tracks, lead, record, save, rewind, open, folderButton, note]);
        var raw = new FlowLayoutPanel { AutoSize = true, Visible = false, Margin = Padding.Empty };
        var rate = new ComboBox { Width=102,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Raw capture rate" };
        rate.Items.AddRange(Radio.SampleRates.Select(r=>$"{r/1e6:0.00} MS/s").ToArray());
        rate.SelectedIndex=Array.IndexOf(Radio.SampleRates,2400000);
        var center = new NumericUpDown { Width=128, DecimalPlaces=6, Increment=.001m, Maximum=1766,
            AccessibleName="Raw capture center in MHz",Value=101.3m };
        var import=Button("Open raw I/Q");
        raw.Controls.AddRange([rate,new Label {Text="Center (MHz)",AutoSize=true},center,import]);
        Controls.Add(raw);
        string? rawPath=null;
        async Task ReadFile(string path,CaptureBand? format)
        {
            open.Enabled=import.Enabled=false;
            try
            {
                await review.OpenFile(path,format);
                raw.Visible=false;
                note.Text=Path.GetFileName(path);
            }
            catch(Exception ex) { if(!IsDisposed) note.Text=ex.Message; }
            finally { if(!IsDisposed) open.Enabled=import.Enabled=true; }
        }
        import.Click+=async (_,_)=>{
            if(rawPath==null) return;
            long hz=(long)(center.Value*1000000);
            await ReadFile(rawPath,new(Radio.SampleRates[rate.SelectedIndex],hz,hz,ListenMode.WFM,200000,true,Guid.Empty));
        };
        note.Text = folder;
        review.ClipChanged += () => save.Text = review.PinnedLive != null ? "Save pinned rewind" : "Save last 30 s";
        folderButton.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { InitialDirectory = folder, Description = "Choose the recordings folder" };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                folder = picker.SelectedPath;
                rememberFolder(AppPaths.SaveRecordingFolder(folder));
                note.Text = folder;
            }
        };
        rewind.Click += (_, _) => { try { review.Pin(tape.Pin()); } catch (InvalidOperationException ex) { note.Text = ex.Message; } };
        open.Click += async (_, _) =>
        {
            using var picker = new OpenFileDialog
            {
                InitialDirectory = Directory.Exists(folder) ? folder : "",
                Filter = "Spectraxia recordings (*.cu8;*.wav)|*.cu8;*.wav",
                Title = "Open a recording"
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            rawPath=picker.FileName;
            raw.Visible=Path.GetExtension(rawPath).Equals(".cu8",StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(RecordingSource.TimelineFor(rawPath));
            if(raw.Visible) note.Text=$"Set the capture rate and center for {Path.GetFileName(rawPath)}.";
            else await ReadFile(rawPath,null);
        };
        record.Click += (_, _) =>
        {
            if (recording is { IsCompleted: false }) { record.Enabled = false; tape.Stop(); note.Text = "Finishing recording…"; return; }
            record.Text = "Stop recording"; tracks.Enabled = lead.Enabled = folderButton.Enabled = false;
            RecordingChanged?.Invoke(true);
            note.Text = $"Recording to {folder}";
            recording = Observe(tape.Start(folder, (CaptureTracks)tracks.SelectedIndex, lead.Checked), true);
        };
        save.Click += (_, _) =>
        {
            var blocks = review.PinnedLive ?? tape.Pin();
            if (blocks.Length == 0) { note.Text = "No received samples to save."; return; }
            var selected = (CaptureTracks)tracks.SelectedIndex; string target = folder;
            save.Enabled = false; note.Text = "Saving recent samples…";
            export = Observe(Task.Run(() => CaptureTape.Export(target, selected, blocks)), false);
        };
    }
    private async Task Observe(Task<string> task, bool live)
    {
        try { string path = await task; note.Text = $"Saved {path}"; }
        catch (Exception ex) { note.Text = ex.Message; }
        finally
        {
            if (live)
            {
                record.Text = "Record";
                record.Enabled = tracks.Enabled = lead.Enabled = folderButton.Enabled = !stopping;
                RecordingChanged?.Invoke(false);
            }
            else save.Enabled = !stopping;
        }
    }
    internal Task Shutdown()
    {
        Enabled = false; stopping = true; tape.Stop();
        return Task.WhenAll(recording ?? Task.CompletedTask, export ?? Task.CompletedTask);
    }
}
