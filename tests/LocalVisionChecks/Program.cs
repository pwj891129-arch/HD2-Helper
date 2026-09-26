using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using HD2_Helper;

internal static class Checks
{
    [STAThread]
    private static void Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        string temporary = Path.Combine(Path.GetTempPath(), "HD2VisionChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        using var recognizer = new LocalVisionRecognizer(root, temporary);
        var timer = Stopwatch.StartNew();
        using var sai = new Bitmap(Path.Combine(root, "images", "Weapons", "LAS-12 사이.png"));
        var vector = recognizer.Embed(sai);
        Assert(vector.Length == 512 && vector.All(float.IsFinite), "finite 512-dimensional neural features");
        Assert(Math.Abs(vector.Sum(x => x * x) - 1) < .001, "unit feature norm");
        var result = recognizer.Recognize(sai, "주 무기", false, CancellationToken.None);
        Assert(result.Candidates[0].Name == "LAS-12 사이" && result.Candidates[0].Similarity > .999f, "Sai self-reference");
        Console.WriteLine($"Cold load and primary reference indexing: {timer.ElapsedMilliseconds} ms, references={result.ReferenceCount}");
        timer.Restart();
        using var reduced = new Bitmap(300, 180);
        using (var graphics = Graphics.FromImage(reduced))
        {
            graphics.Clear(Color.FromArgb(33, 33, 33));
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(sai, new Rectangle(5, 6, 290, 168));
        }
        result = recognizer.Recognize(reduced, "주 무기", false, CancellationToken.None);
        Console.WriteLine($"Resized Sai: {string.Join(", ", result.Candidates.Select(c => $"{c.Name}={c.Similarity:F3}"))}; warm {timer.ElapsedMilliseconds} ms");
        Assert(result.Candidates[0].Name == "LAS-12 사이", "resized Sai retrieval");
        var displayResult = result;
        using var blank = new Bitmap(100, 100);
        Assert(recognizer.Recognize(blank, "주 무기", false, CancellationToken.None).Uncertain, "blank abstention");
        Assert(LocalVisionRecognizer.Rank(new[] { new VisionCandidate("A", .91f, ""), new VisionCandidate("B", .90f, "") }, 2).Uncertain, "ambiguous abstention");
        Assert(LocalVisionRecognizer.Rank(new[] { new VisionCandidate("A", 1f, "") }, 1).Uncertain, "single-label abstention");
        Assert(recognizer.Recognize(sai, "주 무기", true, CancellationToken.None).Candidates.Count == 0, "empty personal reference set");
        recognizer.SaveSample(reduced, recognizer.Items.Single(i => i.Name == "LAS-12 사이"));
        using (var reopened = new LocalVisionRecognizer(root, temporary))
        {
            result = reopened.Recognize(reduced, "주 무기", true, CancellationToken.None);
            Assert(result.Candidates[0].Name == "LAS-12 사이" && result.Candidates[0].Source == "등록 샘플", "sample survives reopening");
            Assert(reopened.Recognize(sai, "투척 무기", true, CancellationToken.None).Candidates.Count == 0, "category isolation");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { recognizer.Recognize(sai, "주 무기", false, cancelled.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS: cancellation"); }
        Assert(recognizer.DeleteSamples(recognizer.Items.Single(i => i.Name == "LAS-12 사이")) == 1, "delete mislabeled samples");
        Assert(recognizer.Recognize(sai, "주 무기", true, CancellationToken.None).Candidates.Count == 0, "deleted samples no longer match");
        Application.EnableVisualStyles();
        using var form = new LocalVisionForm(root, temporary);
        var setImage = typeof(LocalVisionForm).GetMethod("SetImage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setImage.Invoke(form, new object[] { new Bitmap(sai) });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
        form.Show();
        Application.DoEvents();
        var typeControl = (ComboBox)typeof(LocalVisionForm).GetField("_type", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        typeControl.SelectedItem = "주 무기";
        var resultsControl = (ListView)typeof(LocalVisionForm).GetField("_results", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        foreach (var candidate in displayResult.Candidates)
            resultsControl.Items.Add(new ListViewItem(new[] { candidate.Name, candidate.Similarity.ToString("F3"), candidate.Source }));
        form.PerformLayout();
        using var snapshot = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(snapshot, new Rectangle(Point.Empty, snapshot.Size));
        string screenshotPath = Path.Combine(temporary, "local-vision-ui.png");
        snapshot.Save(screenshotPath);
        Console.WriteLine("UI screenshot: " + screenshotPath);
        form.Size = form.MinimumSize;
        Application.DoEvents();
        using var minimum = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(minimum, new Rectangle(Point.Empty, minimum.Size));
        minimum.Save(Path.Combine(temporary, "local-vision-minimum.png"));
        form.Close();
        Console.WriteLine("PASS: local vision checks (synthetic references only; live game accuracy unverified)");
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
