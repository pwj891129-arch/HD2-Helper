using System.Drawing;
using HD2_Helper;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

Check(WindowHeightSizing.Normalize(0) == 0, "Legacy automatic size");
Check(WindowHeightSizing.Normalize(-1) == 430, "Minimum input");
Check(WindowHeightSizing.Normalize(int.MaxValue) == 2160, "Maximum input");
foreach (int width in new[] { 950, 1060 })
foreach (int height in new[] { 540, 630, 840, 777 })
{
    Size size = WindowHeightSizing.Calculate(height, width, 630, new Size(1920, 1000), 760, 430);
    Check(size.Height == height, "Requested height");
    Check(size.Width == (int)Math.Round(height * width / 630d), "Layout aspect ratio");
}
foreach (Size available in new[] { new Size(1920, 964), new Size(1280, 644), new Size(800, 450) })
foreach (int width in new[] { 950, 1060 })
foreach (int baseHeight in new[] { 630, 760, 1150 })
foreach (int requested in new[] { 430, 540, 630, 840, 2160 })
{
    Size size = WindowHeightSizing.Calculate(requested, width, baseHeight, available, 760, 430);
    Check(size.Width <= available.Width && size.Height <= available.Height, "Monitor bounds");
    Check(Math.Abs(size.Width - size.Height * width / (double)baseHeight) <= 0.5, "Rounded ratio");
}
Exception? nativeFailure = null;
var thread = new Thread(() =>
{
    try
    {
        var heightField = typeof(MainForm).GetField("_windowClientHeight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var slotField = typeof(MainForm).GetField("_additionalStratagemSlots", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        heightField.SetValue(null, 630);
        using var editor = new MainForm.HelperEditorWindow(null!);
        editor.ApplyConfiguredWindowHeight();
        Check(editor.ClientSize.Height == 630, "Native editor height");
        Check(editor.ClientSize.Width == 950, "Native editor ratio");
        editor.ApplySettingsPanelClientWidth(1060);
        Check(editor.ClientSize == new Size(1060, 630), "Expanded settings preserve height");
        slotField.SetValue(null, 5);
        editor.ApplyStratagemSlotClientHeight();
        Check(editor.ClientSize == new Size((int)Math.Round(630 * 1060 / 760d), 630), "Extra slots preserve height");
        heightField.SetValue(null, 540);
        editor.ApplyConfiguredWindowHeight();
        Check(editor.ClientSize.Height == 540, "Resize editor again");
    }
    catch (Exception exception) { nativeFailure = exception; }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
if (nativeFailure != null) throw nativeFailure;
Console.WriteLine($"PASS: {checks} window height, layout ratio, monitor-bound and native editor checks.");
