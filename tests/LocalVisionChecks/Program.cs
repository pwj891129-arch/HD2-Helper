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
        Assert(StratagemNames.Canonicalize("고속 정찰 차량") == "M-102 포격 FRV", "legacy vehicle name canonicalized");
        Assert(StratagemNames.Canonicalize("보급 고속 정찰 차량") == "보급 고속 정찰 차량", "supply vehicle not renamed");
        string legacyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("스트라타젬\n고속 정찰 차량")));
        string legacySamples = Path.Combine(temporary, "local-vision", "samples", legacyHash);
        Directory.CreateDirectory(legacySamples);
        using (var vehicle = new Bitmap(Path.Combine(root, "images", "Stratagems", "고속 정찰 차량.png")))
        {
            vehicle.Save(Path.Combine(legacySamples, "previous-version.png"));
            result = recognizer.Recognize(vehicle, "스트라타젬", true, CancellationToken.None);
            Assert(result.Candidates[0].Name == "M-102 포격 FRV", "legacy AI reference remains accessible under new name");
        }
        Application.EnableVisualStyles();
        using var db = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "database.json")));
        var runtimeNames = db.RootElement.GetProperty("스트라타젬").EnumerateObject()
            .Where(c => c.Name != "임무" && c.Name != "패시브")
            .SelectMany(c => c.Value.EnumerateArray().Select(i => i.GetProperty("Name").GetString()!)).ToArray();
        timer.Restart();
        var runtime = new StratagemRuntimeRecognizer(root, runtimeNames);
        Console.WriteLine($"Runtime reference preparation: {timer.ElapsedMilliseconds} ms");
        Assert(StratagemRuntimeRecognizer.Choose(new[] { ("A", .95f), ("B", .94f) }) == null, "runtime rejects close competitors");
        Assert(StratagemRuntimeRecognizer.Choose(new[] { ("A", .83f), ("B", .5f) }) == null, "runtime rejects weak best match");
        Assert(StratagemRuntimeRecognizer.Choose(new[] { ("A", .85f), ("B", .8f) }) == null, "lower score requires wider margin");
        Assert(StratagemRuntimeRecognizer.Choose(new[] { ("A", .85f), ("B", .5f) }) == "A", "distinct small icon accepted");
        Assert(StratagemRuntimeRecognizer.Choose(new[] { ("A", .99f) }) == null, "runtime rejects single candidate");
        var bounds = new Rectangle(10, 10, 100, 100);
        Assert(StratagemRuntimeRecognizer.Stable("A", bounds, "A", bounds, 40), "stable two-frame confirmation");
        Assert(!StratagemRuntimeRecognizer.Stable("A", bounds, "B", bounds, 40), "changed candidate rejected");
        Assert(!StratagemRuntimeRecognizer.Stable("A", bounds, "A", new Rectangle(50, 10, 100, 100), 40), "moving slot rejected");
        Assert(!StratagemRuntimeRecognizer.Stable("A", bounds, "A", bounds, 300), "stale observation rejected");
        Assert(!StratagemRuntimeRecognizer.Stable("A", bounds, "A", bounds, 0), "same-frame repetition rejected");
        Assert(!StratagemRuntimeRecognizer.Stable(null, bounds, null, bounds, 40), "unknown observations rejected");
        Assert(runtime.Match(blank) == null, "runtime blank abstention");
        foreach (var (file, expected) in new[] {
            ("codex-clipboard-446a2d3d-16cb-4edd-8005-f093450396f5.png", "방어막 생성 팩"),
            ("codex-clipboard-e1b47a50-7573-46d2-8d95-1034d1ee6f00.png", "방향 방패") })
        {
            using var capture = new Bitmap(Path.Combine(root, "tests", "LocalVisionChecks", "Fixtures", file));
            timer.Restart();
            result = recognizer.Recognize(capture, "스트라타젬", false, CancellationToken.None);
            Console.WriteLine(string.Join(", ", result.Candidates.Select(c => $"{c.Name}={c.Similarity:F3}")));
            Assert(result.Candidates[0].Name == expected && !result.Uncertain, "actual HUD: " + expected);
            Console.WriteLine($"HUD recognition: {timer.ElapsedMilliseconds} ms");
            timer.Restart();
            Assert(runtime.Match(capture) == expected, "runtime strict HUD: " + expected);
            Console.WriteLine($"Runtime HUD recognition: {timer.ElapsedMilliseconds} ms");
        }
        foreach (string name in new[] { "방향 방패", "방어막 생성 팩", "탄도 방패 배낭", "가드 독", "로버", "핫도그", "작살총", "M-102 포격 FRV", "벌목꾼", "바스티온 MK XVI", "이글 가스 공중타격" })
        {
            using var original = new Bitmap(recognizer.Items.Single(i => i.Type == "스트라타젬" && i.Name == name).ImagePath);
            using var small = new Bitmap(72, 72);
            using (var g = Graphics.FromImage(small))
            {
                g.Clear(Color.FromArgb(33, 33, 33));
                g.DrawImage(original, new Rectangle(5, 7, 60, 60));
            }
            result = recognizer.Recognize(small, "스트라타젬", false, CancellationToken.None);
            Assert(result.Candidates[0].Name == name, "reduced icon: " + name);
            string? runtimeName = runtime.Match(small);
            Assert(runtimeName == null || runtimeName == name, "runtime reduced icon must match or abstain: " + name);
            Console.WriteLine($"Runtime reduced result: {runtimeName ?? "uncertain"}");
        }
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
        Console.WriteLine("PASS: local vision checks (includes two HUD captures; live game automation unverified)");
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
