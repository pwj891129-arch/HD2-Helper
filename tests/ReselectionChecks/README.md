# Reselection checks

Run `dotnet run --project tests/ReselectionChecks -- database.json` from the repository root.

The planner reserves targets already equipped in any slot, then reserves occupied
slots in the same ExclusiveGroup for in-place replacement. Tank, exosuit, and recon
are separate groups (confirmed by the user). Other kinds may coexist. Duplicate
targets and incompatible targets are rejected rather than dropped or reordered
into an impossible loadout. Unrequested slots are retained unless needed.

Execution scans four slots before replacement. Unknown is not empty. Before each
replacement it checks that the observed slot still matches the plan. Selection
requires an identified starting position and target; after pressing select it
reopens the same slot and checks acceptance before continuing. Saved presets are
not modified. Coordinate-only mode is unchanged.

The local neural model remains a diagnostic tool. This executor uses the existing
OCR/icon reader; it does not claim a trained four-slot AI recognizer. Two matching
reads reduce transient errors but cannot rule out systematic misidentification.
Screen slot indexing assumes four evenly spaced slots within the existing
calibrated capture region. Live-game focus transitions and actual recognition
remain to be tested; planner tests do not simulate the game's UI.
