# HUD Regression Fixtures

User-provided crops: 446a2d3d is Shield Generator Pack; e1b47a50 is Directional Shield.
These preserve the small arrow detail lost by global neural descriptors.
The checks require correct top candidates and a non-ambiguous score margin.

Stratagem retrieval compares aligned white markings (70%) and colored silhouettes
(30%) independently. This is template matching, not a newly trained neural model.
Scores are not probabilities. Gates remain experimental; these two captures and
resized references do not establish accuracy across all resolutions or HUD states.
Select one complete icon without neighboring icons; occlusion, bright backgrounds,
and missing detail may require a new capture.

2.0.35.11-test also uses cached built-in references for automatic stratagem
reselection. Runtime acceptance requires score/margin of 0.88/0.06 or 0.84/0.12, includes
excluded items as competitors, and requires stable observations before selecting.
`equipped-four.png` is a user-provided four-slot header crop; RuntimeSelectionChecks
exercises the production border finder and classifier without sending game input.

2.0.35.12-test feeds explicit Unknown/Empty/Equipped observations into the planner.
ReselectionChecks covers initial selection, mixed slots, replacement, and no-op
plans. Empty observations need three consistent reads; equipped icons need two.
Unmatched visible content and missing borders cannot authorize an empty-slot plan.
Perception determines the slot state; deterministic planning, not a language model,
enforces duplicate and same-kind equipment restrictions. Live game transitions
remain unverified by these offline checks.
