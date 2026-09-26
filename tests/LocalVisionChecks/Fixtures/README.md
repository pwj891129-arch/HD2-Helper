# HUD Regression Fixtures

User-provided crops: 446a2d3d is Shield Generator Pack; e1b47a50 is Directional Shield.
These preserve the small arrow detail lost by global neural descriptors.
The checks require correct top candidates and a non-ambiguous score margin.

Stratagem retrieval compares aligned white markings (70%) and colored silhouettes
(30%) independently. This is template matching, not a newly trained neural model.
Scores are not probabilities. Gates remain experimental; these two captures and
resized references do not establish accuracy across all resolutions or HUD states.
Select one complete icon without neighboring icons; occlusion, bright backgrounds,
and missing detail may require a new capture. Automatic reselection is unchanged.
