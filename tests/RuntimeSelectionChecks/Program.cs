using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

string root = Path.GetFullPath(args[0]);
var assembly = Assembly.Load("HD2 Helper");
var main = assembly.GetType("HD2_Helper.MainForm", true)!;
var find = main.GetMethod("TryFindSelectedSlotRegion", BindingFlags.Static | BindingFlags.NonPublic)!;
var runtimeType = assembly.GetType("HD2_Helper.StratagemRuntimeRecognizer", true)!;
using var db = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "database.json")));
var names = db.RootElement.GetProperty("스트라타젬").EnumerateObject()
    .Where(c => c.Name != "임무" && c.Name != "패시브")
    .SelectMany(c => c.Value.EnumerateArray().Select(i => i.GetProperty("Name").GetString()!)).ToArray();
var runtime = Activator.CreateInstance(runtimeType, root, names)!;
var match = runtimeType.GetMethod("Match")!;
using var screenshot = new Bitmap(Path.Combine(root, "tests", "LocalVisionChecks", "Fixtures", "equipped-four.png"));
var timer = Stopwatch.StartNew();
var selected = (Rectangle?)find.Invoke(null, new object[] { screenshot });
if (selected == null) throw new Exception("Selected slot not found");
var inner = Rectangle.Intersect(Rectangle.Inflate(selected.Value, -Math.Max(4, selected.Value.Width / 18), -Math.Max(4, selected.Value.Height / 18)), new Rectangle(Point.Empty, screenshot.Size));
using var crop = screenshot.Clone(inner, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
var actual = (string?)match.Invoke(runtime, new object[] { crop });
Console.WriteLine($"Selected slot={selected}, name={actual}, detection + classification={timer.ElapsedMilliseconds} ms");
if (actual != "M-102 포격 FRV") throw new Exception("Wrong selected header icon");
foreach (var (box, expected) in new[] {
    (new Rectangle(19, 15, 92, 92), "작살총"),
    (new Rectangle(130, 15, 97, 92), "M-102 포격 FRV"),
    (new Rectangle(245, 15, 92, 92), "벌목꾼"),
    (new Rectangle(358, 15, 92, 92), "바스티온 MK XVI") })
{
    using var icon = screenshot.Clone(box, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    string? found = (string?)match.Invoke(runtime, new object[] { icon });
    Console.WriteLine($"Header icon: expected={expected}, actual={found ?? "uncertain"}");
    var scores = ((ValueTuple<string, float>[])runtimeType.GetMethod("Rank")!.Invoke(runtime, new object[] { icon })!);
    Console.WriteLine(string.Join(", ", scores.Select(s => $"{s.Item1}={s.Item2:F3}")));
    if (found != expected) throw new Exception("Header icon failed: " + expected);
}
using var blank = new Bitmap(screenshot.Width, screenshot.Height);
if (find.Invoke(null, new object[] { blank }) != null) throw new Exception("Blank detected as selected slot");
var timings = new List<double>();
using var search = new Bitmap(430, 650);
using (var g = Graphics.FromImage(search)) { g.Clear(Color.FromArgb(33, 33, 33)); g.DrawImageUnscaled(screenshot, 0, 20); }
for (int i = 0; i < 10; i++)
{
    timer.Restart();
    var box = (Rectangle?)find.Invoke(null, new object[] { search });
    if (box == null) throw new Exception("Search-region slot missing");
    var region = Rectangle.Inflate(box.Value, -Math.Max(4, box.Value.Width / 18), -Math.Max(4, box.Value.Height / 18));
    using var icon = search.Clone(region, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    if ((string?)match.Invoke(runtime, new object[] { icon }) != "M-102 포격 FRV") throw new Exception("Search-region match failed");
    timings.Add(timer.Elapsed.TotalMilliseconds);
}
Console.WriteLine($"430x650 in-memory region detection + matching: median={timings.Order().ElementAt(5):F1} ms, max={timings.Max():F1} ms (screen capture and key delays excluded)");
Console.WriteLine("PASS: production slot detection and cached matching on actual four-slot screenshot; no game input sent");
