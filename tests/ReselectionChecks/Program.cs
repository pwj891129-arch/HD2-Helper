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
Assert(vehicles["M-102 포격 FRV"] == vehicles["보급 고속 정찰 차량"], "Recon metadata");
Assert(vehicles.Values.Distinct().Count() == 3, "Vehicle kinds must remain separate");
Console.WriteLine("PASS: database separates tank, exosuit, and recon groups");

StratagemSlotObservation[] Observe(params string?[] names) => names.Select((n, i) =>
    StratagemSlotObservation.FromEvidence(i, true, n, n == null ? 0 : 100)).ToArray();
var fresh = StratagemReselectionPlanner.Build(Observe(null, null, null, null), Targets("A", "B", "C", "D"), groups);
Assert(fresh.Mode == StratagemPlanMode.InitialSelection && fresh.Replacements.Count == 4
    && fresh.Decisions.All(d => d.Action == StratagemSelectionAction.FillEmpty), "All empty: initial selection");
var mixed = StratagemReselectionPlanner.Build(Observe("C", null, "A", null), Targets("A", "B"), groups);
Assert(mixed.Mode == StratagemPlanMode.Mixed && mixed.Replacements.Count == 1
    && mixed.Replacements[0].SlotIndex == 1 && mixed.FinalSlots[0] == "C", "Mixed: keep target elsewhere, fill empty instead of replacing");
var conflict = StratagemReselectionPlanner.Build(Observe("Recon1", null, "A", null), Targets("Recon2", "B", "A"), groups);
Assert(conflict.Replacements[0].Action == StratagemSelectionAction.FillEmpty
    && conflict.Replacements[1].SlotIndex == 0 && conflict.Replacements[1].Action == StratagemSelectionAction.Replace,
    "Fill empty first; replace same-kind vehicle in its original slot");
Validate(new string?[] { "Recon1", null, "A", null }, Targets("Recon2", "B", "A"), conflict);
var full = StratagemReselectionPlanner.Build(Observe("A", "B", "C", "D"), Targets("A", "B", "C", "E"), groups);
Assert(full.Mode == StratagemPlanMode.Reselection && full.Replacements.Count == 1, "Full: only necessary replacement");
var satisfied = StratagemReselectionPlanner.Build(Observe("B", "A", null, null), Targets("A", "B"), groups);
Assert(satisfied.Mode == StratagemPlanMode.AlreadySatisfied && satisfied.Replacements.Count == 0
    && satisfied.Decisions.All(d => d.Action == StratagemSelectionAction.Keep), "Already satisfied: no selection");
var uncertain = Observe(null, null, null, null);
uncertain[1] = StratagemSlotObservation.FromEvidence(1, true, null, 1);
Assert(!uncertain[1].Known, "Unrecognized visible content is not empty");
Reject(() => StratagemReselectionPlanner.Build(uncertain, Targets("A"), groups));
Assert(!StratagemSlotObservation.FromEvidence(0, false, null, 0).Known, "Missing border is not empty");
Reject(() => StratagemReselectionPlanner.Build(new[] { uncertain[0], uncertain[0], uncertain[2], uncertain[3] }, Targets("A"), groups));
var consensus = new StratagemSlotConsensus();
var empty = Observe(null, null, null, null)[0];
Assert(!consensus.Observe(empty, 0, 0) && !consensus.Observe(empty, 0, 40) && consensus.Observe(empty, 0, 80), "Empty needs three stable observations");
var equipped = Observe("A", null, null, null)[0];
Assert(!consensus.Observe(equipped, 0, 120) && consensus.Observe(equipped, 0, 160), "Equipped needs two observations after transition");
Assert(!consensus.Observe(equipped, 1, 200) && !consensus.Observe(equipped, 0, 240), "Wrong slot resets consensus");
Assert(!consensus.Observe(equipped, 0, 600), "Stale observations reset consensus");
Assert(!consensus.Observe(new(0, StratagemSlotState.Unknown, null), 0, 640)
    && !consensus.Observe(equipped, 0, 680), "Unknown resets consensus");
Console.WriteLine("PASS: initial/mixed/reselection/already-satisfied decisions, empty priority, unknown refusal and temporal state consensus");
