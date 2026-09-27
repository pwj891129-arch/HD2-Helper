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
using var emptyHeader = new Bitmap(Path.Combine(root, "tests", "LocalVisionChecks", "Fixtures", "equipped-empty.png"));
var emptyBox = (Rectangle?)find.Invoke(null, new object[] { emptyHeader });
if (emptyBox == null) throw new Exception("Empty selected slot not located");
var contentRegionMethod = main.GetMethod("GetEquippedSlotContentRegion", BindingFlags.Static | BindingFlags.NonPublic)!;
var emptyInner = (Rectangle)contentRegionMethod.Invoke(null, new object[] { emptyBox.Value, emptyHeader.Size })!;
var classify = main.GetMethod("TryClassifyEquippedSlotContentPixel", BindingFlags.Static | BindingFlags.NonPublic)!;
int contentCount = 0;
for (int y = emptyInner.Top; y < emptyInner.Bottom; y++)
    for (int x = emptyInner.Left; x < emptyInner.Right; x++)
        if ((bool)classify.Invoke(null, new object[] { emptyHeader.GetPixel(x, y), "" })!) contentCount++;
Console.WriteLine($"Actual empty slot: bounds={emptyBox}, foregroundPixels={contentCount}");
if (contentCount != 0) throw new Exception("Empty slot polluted by frame/background");
var wideEmpty = Rectangle.Inflate(emptyBox.Value, -Math.Max(4, emptyBox.Value.Width / 18), -Math.Max(4, emptyBox.Value.Height / 18));
using var wideEmptyCrop = emptyHeader.Clone(wideEmpty, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
if (match.Invoke(runtime, new object[] { wideEmptyCrop }) != null) throw new Exception("Empty slot falsely identified as equipment");
Console.WriteLine("PASS: actual empty slot remains empty despite bright pixels outside its content interior");
var menuState = main.GetMethod("ResolveStratagemMenuState", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (var (cursor, menu, prep, expected) in new[] {
    (true, true, true, (bool?)true), (true, false, true, (bool?)null),
    (false, false, true, (bool?)false), (false, false, false, (bool?)null) })
    if ((bool?)menuState.Invoke(null, new object[] { cursor, menu, prep }) != expected)
        throw new Exception("Menu identity/presence separation failed");
Console.WriteLine("PASS: unknown highlighted item cannot suppress positive menu evidence or imply closed menu");
using var initialScreen = new Bitmap(Path.Combine(root, "tests", "LocalVisionChecks", "Fixtures", "empty-initial-screen.png"));
var headerRect = new Rectangle(60, 333, 480, 200);
using var initialHeader = initialScreen.Clone(headerRect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
var initialBox = (Rectangle?)find.Invoke(null, new object[] { initialHeader });
Console.WriteLine($"Initial empty screen selected header={initialBox}");
if (initialBox == null) throw new Exception("Initial empty selected header not found");
var initialInterior = (Rectangle)contentRegionMethod.Invoke(null, new object[] { initialBox.Value, initialHeader.Size })!;
int initialPixels = 0;
for (int y = initialInterior.Top; y < initialInterior.Bottom; y++)
    for (int x = initialInterior.Left; x < initialInterior.Right; x++)
        if ((bool)classify.Invoke(null, new object[] { initialHeader.GetPixel(x, y), "" })!) initialPixels++;
Console.WriteLine($"Initial empty foreground={initialPixels}, interior={initialInterior}");
if (initialPixels != 0) throw new Exception("Initial empty content polluted");
if ((initialBox.Value.Left + initialBox.Value.Width / 2) * 4 / initialHeader.Width != 0)
    throw new Exception("Wrong initial slot index");
using var initialList = initialScreen.Clone(new Rectangle(73, 520, 573, 867), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
var listBox = (Rectangle?)find.Invoke(null, new object[] { initialList });
if (listBox == null) throw new Exception("Initial highlighted list item not located");
var listInterior = Rectangle.Inflate(listBox.Value, -Math.Max(4, listBox.Value.Width / 18), -Math.Max(4, listBox.Value.Height / 18));
using var initialIcon = initialList.Clone(listInterior, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
var initialName = (string?)match.Invoke(runtime, new object[] { initialIcon });
Console.WriteLine($"Initial list highlight={listBox}, item={initialName ?? "uncertain"}");
if (initialName != "궤도 정밀 타격") throw new Exception("Initial cursor identity failed");
using var incompleteFrame = new Bitmap(200, 180);
using (var g = Graphics.FromImage(incompleteFrame))
using (var pen = new Pen(Color.Yellow, 3))
{
    g.Clear(Color.FromArgb(33, 33, 33));
    g.DrawLine(pen, 40, 40, 140, 40);
    g.DrawLine(pen, 40, 40, 40, 140);
    g.DrawLine(pen, 140, 40, 140, 140);
}
if (find.Invoke(null, new object[] { incompleteFrame }) != null) throw new Exception("Incomplete frame accepted");
Console.WriteLine("PASS: initial empty screenshot and list cursor recognized; incomplete border rejected");
using var railgunScreen = new Bitmap(Path.Combine(root, "tests", "LocalVisionChecks", "Fixtures", "selected-railgun-screen.png"));
using var railgunList = railgunScreen.Clone(new Rectangle(73, 520, 573, 867), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
var railgunBox = (Rectangle?)find.Invoke(null, new object[] { railgunList });
Console.WriteLine($"Railgun list box={railgunBox}");
if (railgunBox == null) throw new Exception("Railgun highlight missing");
var railgunInterior = Rectangle.Inflate(railgunBox.Value, -Math.Max(4, railgunBox.Value.Width / 18), -Math.Max(4, railgunBox.Value.Height / 18));
using var railgunIcon = railgunList.Clone(railgunInterior, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
Console.WriteLine(string.Join(", ", ((ValueTuple<string, float>[])runtimeType.GetMethod("Rank")!.Invoke(runtime, new object[] { railgunIcon })!).Select(s => $"{s.Item1}={s.Item2:F3}")));
if ((string?)match.Invoke(runtime, new object[] { railgunIcon }) != "레일건") throw new Exception("Dim equipped railgun not recognized");
