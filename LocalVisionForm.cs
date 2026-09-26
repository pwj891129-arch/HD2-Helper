using System.Diagnostics;
using System.Drawing.Imaging;

namespace HD2_Helper;

internal sealed class LocalVisionForm : Form
{
    private readonly LocalVisionRecognizer _recognizer;
    private readonly ComboBox _type = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    private readonly ComboBox _monitor = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _label = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly CheckBox _samplesOnly = new() { Text = "등록 샘플만", AutoSize = true };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(24, 24, 24) };
    private readonly ListView _results = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = "대기" };
    private readonly List<Control> _actions = new();
    private readonly ToolTip _tips = new();
    private readonly CancellationTokenSource _cancel = new();
    private Rectangle? _region;
    private Bitmap? _image;
    private bool _busy;

    public LocalVisionForm(string root, string appData)
    {
        _recognizer = new(root, appData);
        Text = "로컬 AI 장비 인식 (테스트)";
        Size = new Size(1000, 680);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("맑은 고딕", 10);
        BackColor = Color.FromArgb(33, 33, 33);
        ForeColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        Controls.Add(layout);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        toolbar.Controls.Add(_type);
        toolbar.Controls.Add(_monitor);
        AddButton(toolbar, "영역 선택", CaptureRegionAsync);
        AddButton(toolbar, "다시 캡처", CaptureAgainAsync);
        AddButton(toolbar, "이미지 열기", OpenImageAsync);
        AddButton(toolbar, "분석", AnalyzeAsync);
        toolbar.Controls.Add(_samplesOnly);
        _actions.AddRange(new Control[] { _type, _monitor, _samplesOnly, _label });
        layout.Controls.Add(toolbar, 0, 0);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        split.Controls.Add(_preview, 0, 0);
        _results.Columns.Add("후보", 245);
        _results.Columns.Add("유사도", 80);
        _results.Columns.Add("기준", 100);
        split.Controls.Add(_results, 1, 0);
        layout.Controls.Add(split, 0, 1);
        var samples = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        samples.Controls.Add(_label);
        AddButton(samples, "샘플 저장", SaveSampleAsync);
        AddButton(samples, "샘플 삭제", DeleteSamplesAsync);
        layout.Controls.Add(samples, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        _tips.SetToolTip(_results, "스트라타젬은 세부 아이콘 일치도, 장비는 모델 특징 유사도입니다. 정확도 확률이 아닙니다.");
        _tips.SetToolTip(_samplesOnly, "기본 장비 이미지를 제외하고 직접 등록한 샘플만 비교합니다.");
        foreach (string type in _recognizer.Items.Select(i => i.Type).Distinct()) _type.Items.Add(type);
        _type.SelectedIndexChanged += (_, _) => {
            _label.Items.Clear();
            foreach (var item in _recognizer.Items.Where(i => i.Type == (string)_type.SelectedItem!)) _label.Items.Add(item.Name);
            if (_label.Items.Count > 0) _label.SelectedIndex = 0;
            _results.Items.Clear();
        };
        _type.SelectedIndex = 0;
        foreach (var screen in Screen.AllScreens) _monitor.Items.Add($"{screen.DeviceName} ({screen.Bounds.Width} x {screen.Bounds.Height})");
        _monitor.SelectedIndex = 0;
        _monitor.SelectedIndexChanged += (_, _) => _region = null;
        FormClosing += (_, e) => { if (_busy) { _cancel.Cancel(); e.Cancel = true; _status.Text = "작업 취소 중"; } };
        FormClosed += (_, _) => { _recognizer.Dispose(); _image?.Dispose(); _tips.Dispose(); _cancel.Dispose(); };
    }

    private void AddButton(FlowLayoutPanel panel, string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(62, 62, 62), ForeColor = Color.White };
        if (text == "영역 선택") _tips.SetToolTip(button, "3초 뒤 선택한 모니터에서 장비 아이콘 영역을 드래그합니다. Esc로 취소합니다.");
        if (text == "다시 캡처") _tips.SetToolTip(button, "3초 뒤 이전에 선택한 영역을 다시 캡처합니다.");
        button.Click += async (_, _) => {
            if (_busy) return;
            _busy = true;
            foreach (var control in _actions) control.Enabled = false;
            try { await action(); }
            catch (OperationCanceledException) { _status.Text = "취소됨"; }
            catch (Exception ex) { _status.Text = "실패: " + ex.Message; MessageBox.Show(this, ex.Message, "로컬 AI 인식", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally {
                _busy = false;
                foreach (var control in _actions) control.Enabled = true;
                if (_cancel.IsCancellationRequested) Close();
            }
        };
        _actions.Add(button);
        panel.Controls.Add(button);
    }

    private void SetImage(Bitmap bitmap)
    {
        _preview.Image = null;
        _image?.Dispose();
        _image = bitmap;
        _preview.Image = bitmap;
        _results.Items.Clear();
        _status.Text = $"입력 {bitmap.Width} x {bitmap.Height}";
    }

    private async Task CaptureRegionAsync()
    {
        Rectangle bounds = Screen.AllScreens[_monitor.SelectedIndex].Bounds;
        Hide();
        try {
            await Task.Delay(3000, _cancel.Token);
            using var screenshot = CaptureScreenRegion(bounds);
            using var selector = new VisionRegionSelector(screenshot, bounds);
            if (selector.ShowDialog() == DialogResult.OK)
            {
                var crop = selector.SelectedRegion;
                _region = new Rectangle(bounds.X + crop.X, bounds.Y + crop.Y, crop.Width, crop.Height);
                SetImage(screenshot.Clone(crop, PixelFormat.Format24bppRgb));
            }
        }
        finally { Show(); Activate(); }
    }

    private async Task CaptureAgainAsync()
    {
        if (_region is not Rectangle region) { _status.Text = "선택된 영역 없음"; return; }
        if (!Screen.AllScreens.Any(s => s.Bounds.Contains(region))) { _region = null; _status.Text = "화면 구성이 변경됨"; return; }
        Hide();
        try { await Task.Delay(3000, _cancel.Token); SetImage(CaptureScreenRegion(region)); }
        finally { Show(); Activate(); }
    }

    private static Bitmap CaptureScreenRegion(Rectangle bounds)
    {
        var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try { using var graphics = Graphics.FromImage(image); graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size); return image; }
        catch { image.Dispose(); throw; }
    }

    private Task OpenImageAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            using var original = new Bitmap(dialog.FileName);
            SetImage(new Bitmap(original));
        }
        return Task.CompletedTask;
    }

    private async Task AnalyzeAsync()
    {
        if (_image == null) { _status.Text = "입력 이미지 없음"; return; }
        _results.Items.Clear();
        _status.Text = "분석 중";
        using var query = new Bitmap(_image);
        string type = (string)_type.SelectedItem!;
        bool samplesOnly = _samplesOnly.Checked;
        var timer = Stopwatch.StartNew();
        var result = await Task.Run(() => _recognizer.Recognize(query, type, samplesOnly, _cancel.Token));
        foreach (var candidate in result.Candidates)
            _results.Items.Add(new ListViewItem(new[] { candidate.Name, candidate.Similarity.ToString("0.000"), candidate.Source }));
        string verdict = result.Candidates.Count == 0 ? "비교 샘플 없음" : result.Uncertain ? "판별 보류" : "유력 후보: " + result.Candidates[0].Name;
        _status.Text = $"{verdict} | {timer.ElapsedMilliseconds:N0} ms | 기준 {result.ReferenceCount}개 | 자동 입력 없음";
    }

    private Task SaveSampleAsync()
    {
        if (_image == null || _label.SelectedItem == null) { _status.Text = "입력 이미지 또는 장비 없음"; return Task.CompletedTask; }
        var item = _recognizer.Items.Single(i => i.Type == (string)_type.SelectedItem! && i.Name == (string)_label.SelectedItem);
        if (MessageBox.Show(this, $"현재 이미지를 '{item.Name}' 기준 샘플로 저장할까요?", "샘플 등록", MessageBoxButtons.YesNo) == DialogResult.Yes)
        {
            _recognizer.SaveSample(_image, item);
            _status.Text = "샘플 저장됨: " + item.Name;
        }
        return Task.CompletedTask;
    }

    private Task DeleteSamplesAsync()
    {
        if (_label.SelectedItem == null) return Task.CompletedTask;
        var item = _recognizer.Items.Single(i => i.Type == (string)_type.SelectedItem! && i.Name == (string)_label.SelectedItem);
        if (MessageBox.Show(this, $"'{item.Name}'에 직접 등록한 샘플을 모두 삭제할까요?", "샘플 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            _status.Text = $"샘플 {_recognizer.DeleteSamples(item)}개 삭제됨";
        return Task.CompletedTask;
    }
}

internal sealed class VisionRegionSelector : Form
{
    private readonly Bitmap _screenshot;
    private Point? _start;
    public Rectangle SelectedRegion { get; private set; }
    public VisionRegionSelector(Bitmap screenshot, Rectangle bounds)
    {
        _screenshot = screenshot;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = bounds;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _start = e.Location; Capture = true; } };
        MouseMove += (_, e) => {
            if (_start is not Point start) return;
            SelectedRegion = Rectangle.Intersect(ClientRectangle, Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y)));
            Invalidate();
        };
        MouseUp += (_, e) => {
            if (e.Button != MouseButtons.Left) return;
            Capture = false;
            if (SelectedRegion.Width >= 16 && SelectedRegion.Height >= 16) { DialogResult = DialogResult.OK; Close(); }
            else _start = null;
        };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_screenshot, 0, 0);
        using var pen = new Pen(Color.Lime, 2);
        if (!SelectedRegion.IsEmpty) e.Graphics.DrawRectangle(pen, SelectedRegion);
    }
}
