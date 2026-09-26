namespace HD2_Helper;

internal sealed record StratagemSelectionTarget(string Name, int SlotIndex);
internal enum StratagemPlanMode { InitialSelection, Mixed, Reselection, AlreadySatisfied }
internal enum StratagemSelectionAction { Keep, FillEmpty, Replace }
internal sealed record StratagemReplacement(string Name, int SlotIndex, string? PreviousName)
{
    public StratagemSelectionAction Action => PreviousName == null ? StratagemSelectionAction.FillEmpty : StratagemSelectionAction.Replace;
}
internal sealed record StratagemSelectionDecision(string Name, int SlotIndex, StratagemSelectionAction Action);
internal sealed record StratagemReselectionPlan(IReadOnlyList<StratagemReplacement> Replacements, string?[] FinalSlots,
    StratagemPlanMode Mode, IReadOnlyList<StratagemSelectionDecision> Decisions);

internal static class StratagemReselectionPlanner
{
    public static StratagemReselectionPlan Build(IReadOnlyList<StratagemSlotObservation> observations,
        IReadOnlyList<StratagemSelectionTarget> targets, IReadOnlyDictionary<string, string> groups)
    {
        if (observations.Count != 4 || observations.Select(o => o.SlotIndex).Order().SequenceEqual(new[] { 0, 1, 2, 3 }) == false
            || observations.Any(o => !o.Known || (o.State == StratagemSlotState.Equipped ? string.IsNullOrWhiteSpace(o.Name) : o.Name != null)))
            throw new InvalidOperationException("빈칸 또는 장착 상태가 확실하지 않은 칸이 있습니다. 선택을 중단합니다.");
        return Build(observations.OrderBy(o => o.SlotIndex).Select(o => o.Name).ToArray(), targets, groups);
    }

    public static void ValidateTargets(IReadOnlyList<StratagemSelectionTarget> targets, IReadOnlyDictionary<string, string> groups)
    {
        if (targets.Count > 4 || targets.Any(t => string.IsNullOrWhiteSpace(t.Name)))
            throw new InvalidOperationException("스트라타젬 목표는 최대 4개여야 합니다.");
        if (targets.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new InvalidOperationException("목표 프리셋에 같은 스트라타젬이 중복되어 있습니다.");
        foreach (var group in targets.Where(t => groups.ContainsKey(t.Name)).GroupBy(t => groups[t.Name]))
            if (group.Count() > 1)
                throw new InvalidOperationException("동시에 장착할 수 없는 목표입니다: " + string.Join(", ", group.Select(t => t.Name)));
    }

    public static StratagemReselectionPlan Build(IReadOnlyList<string?> equipped,
        IReadOnlyList<StratagemSelectionTarget> targets, IReadOnlyDictionary<string, string> groups)
    {
        if (equipped.Count != 4) throw new InvalidOperationException("장착 슬롯 4개를 모두 확인해야 합니다.");
        ValidateTargets(targets, groups);
        var current = equipped.Select(n => string.IsNullOrWhiteSpace(n) ? null : n).ToArray();
        var occupied = current.Where(n => n != null).Select(n => n!).ToArray();
        if (occupied.Distinct(StringComparer.Ordinal).Count() != occupied.Length)
            throw new InvalidOperationException("중복 장착으로 인식되었습니다. 슬롯 인식을 다시 확인해 주세요.");
        foreach (var group in occupied.Where(groups.ContainsKey).GroupBy(n => groups[n]))
            if (group.Count() > 1)
                throw new InvalidOperationException("동시 장착 제한과 맞지 않는 슬롯 인식입니다: " + string.Join(", ", group));

        var reserved = new HashSet<int>();
        var assigned = new Dictionary<string, int>(StringComparer.Ordinal);
        // Preserve exact matches anywhere before assigning replacements to avoid duplicates.
        foreach (var target in targets)
        {
            int existing = Array.IndexOf(current, target.Name);
            if (existing >= 0) { reserved.Add(existing); assigned.Add(target.Name, existing); }
        }
        // Reserve conflict slots first, so an unrelated target cannot consume them.
        foreach (var target in targets.Where(t => !assigned.ContainsKey(t.Name)))
        {
            if (!groups.TryGetValue(target.Name, out string? group)) continue;
            int conflict = Array.FindIndex(current, n => n != null && groups.TryGetValue(n, out var existingGroup) && existingGroup == group);
            if (conflict < 0) continue;
            if (!reserved.Add(conflict)) throw new InvalidOperationException("동시 장착 제한 칸을 배정할 수 없습니다.");
            assigned.Add(target.Name, conflict);
        }
        foreach (var target in targets.Where(t => !assigned.ContainsKey(t.Name)))
        {
            int slot = Enumerable.Range(0, 4).Where(i => !reserved.Contains(i))
                .OrderBy(i => current[i] != null).ThenBy(i => i != target.SlotIndex).ThenBy(i => i).First();
            reserved.Add(slot);
            assigned.Add(target.Name, slot);
        }
        var final = current.ToArray();
        var operations = new List<StratagemReplacement>();
        foreach (var target in targets)
        {
            int slot = assigned[target.Name];
            final[slot] = target.Name;
            if (current[slot] != target.Name) operations.Add(new(target.Name, slot, current[slot]));
        }
        var mode = operations.Count == 0 ? StratagemPlanMode.AlreadySatisfied
            : occupied.Length == 0 ? StratagemPlanMode.InitialSelection
            : occupied.Length == 4 ? StratagemPlanMode.Reselection : StratagemPlanMode.Mixed;
        var decisions = targets.Select(t => new StratagemSelectionDecision(t.Name, assigned[t.Name],
            current[assigned[t.Name]] == t.Name ? StratagemSelectionAction.Keep
            : current[assigned[t.Name]] == null ? StratagemSelectionAction.FillEmpty : StratagemSelectionAction.Replace)).ToArray();
        return new(operations.OrderBy(o => o.PreviousName != null).ThenBy(o => o.SlotIndex).ToList(), final, mode, decisions);
    }
}
