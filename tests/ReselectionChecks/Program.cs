using System.Text.Json;
using HD2_Helper;

var groups = new Dictionary<string, string> {
    ["Exo1"] = "exo", ["Exo2"] = "exo", ["Recon1"] = "recon", ["Recon2"] = "recon", ["Tank"] = "tank"
};
StratagemSelectionTarget[] Targets(params string[] names) => names.Select((n, i) => new StratagemSelectionTarget(n, i)).ToArray();
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
bool Valid(IEnumerable<string?> slots) {
    var names = slots.Where(n => n != null).Select(n => n!).ToArray();
    return names.Distinct().Count() == names.Length && names.Where(groups.ContainsKey).GroupBy(n => groups[n]).All(g => g.Count() <= 1);
}
void Validate(string?[] current, StratagemSelectionTarget[] requested, StratagemReselectionPlan plan) {
    var live = current.ToArray();
    foreach (var op in plan.Replacements) {
        Assert(live[op.SlotIndex] == op.PreviousName, "Expected previous name");
        if (groups.TryGetValue(op.Name, out var group)) {
            int conflict = Array.FindIndex(current, n => n != null && groups.TryGetValue(n, out var g) && g == group);
            if (conflict >= 0) Assert(conflict == op.SlotIndex, "Conflict must be replaced in its own slot");
        }
        live[op.SlotIndex] = op.Name;
        Assert(Valid(live), "Intermediate state violates exclusivity or duplicates");
    }
    Assert(live.SequenceEqual(plan.FinalSlots), "Final slots mismatch");
    foreach (var target in requested) {
        Assert(live.Contains(target.Name), "Requested equipment missing");
        int oldSlot = Array.IndexOf(current, target.Name);
        if (oldSlot >= 0) Assert(live[oldSlot] == target.Name, "Existing target moved");
    }
}

var current = new string?[] { "Tank", "Exo1", "Recon1", "A" };
var targets = Targets("Exo2", "Recon2", "A", "Tank");
var plan = StratagemReselectionPlanner.Build(current, targets, groups);
Assert(plan.Replacements.Count == 2 && plan.Replacements[0].SlotIndex == 1 && plan.Replacements[1].SlotIndex == 2, "Same-kind replacement");
Validate(current, targets, plan);
Console.WriteLine("PASS: tank retained; exosuit and recon replaced in their own slots");
current = new string?[] { "A", "C", "B", "E" };
targets = Targets("A", "B", "C", "D");
plan = StratagemReselectionPlanner.Build(current, targets, groups);
Assert(plan.Replacements.Count == 1 && plan.Replacements[0].SlotIndex == 3, "Already equipped targets should not be reselected");
Validate(current, targets, plan);
Console.WriteLine("PASS: targets in other slots preserved");
Reject(() => StratagemReselectionPlanner.Build(new string?[4], Targets("Exo1", "Exo2"), groups));
Reject(() => StratagemReselectionPlanner.Build(new string?[4], Targets("A", "A"), groups));
Reject(() => StratagemReselectionPlanner.Build(new string?[] { "A", "A", null, null }, Targets("B"), groups));
Reject(() => StratagemReselectionPlanner.Build(new string?[] { "Exo1", "Exo2", null, null }, Targets("B"), groups));
Console.WriteLine("PASS: conflicting targets and impossible observations rejected");
string?[] domain = { null, "A", "B", "C", "Exo1", "Exo2", "Recon1", "Recon2", "Tank" };
var random = new Random(42);
int checkedPlans = 0;
for (int iteration = 0; iteration < 30000; iteration++) {
    current = Enumerable.Range(0, 4).Select(_ => domain[random.Next(domain.Length)]).ToArray();
    var requested = Enumerable.Range(0, random.Next(1, 5)).Select(_ => domain[random.Next(1, domain.Length)]!).ToArray();
    if (!Valid(current) || !Valid(requested)) continue;
    targets = Targets(requested);
    var before = current.ToArray();
    plan = StratagemReselectionPlanner.Build(current, targets, groups);
    Assert(before.SequenceEqual(current), "Input array modified");
    Validate(current, targets, plan);
    checkedPlans++;
}
Console.WriteLine($"PASS: {checkedPlans} deterministic randomized plans, each intermediate state checked");
using var db = JsonDocument.Parse(File.ReadAllText(args[0]));
var vehicles = db.RootElement.GetProperty("스트라타젬").GetProperty("보급").EnumerateArray()
    .Where(x => x.TryGetProperty("ExclusiveGroup", out _)).ToDictionary(x => x.GetProperty("Name").GetString()!, x => x.GetProperty("ExclusiveGroup").GetString()!);
Assert(vehicles["해방자 엑소슈트"] == vehicles["애국자 엑소슈트"], "Exosuit metadata");
Assert(vehicles["고속 정찰 차량"] == vehicles["보급 고속 정찰 차량"], "Recon metadata");
Assert(vehicles.Values.Distinct().Count() == 3, "Vehicle kinds must remain separate");
Console.WriteLine("PASS: database separates tank, exosuit, and recon groups");
