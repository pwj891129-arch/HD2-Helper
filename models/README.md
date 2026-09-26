# Local Vision Test

The test uses ONNX Model Zoo ResNet18-v1 (ImageNet pretrained) with its classifier
removed. It returns a 512-dimensional pooled visual descriptor. This is reference
retrieval, NOT a model trained to identify Helldivers equipment or understand menus.
Source: https://github.com/onnx/models/tree/main/validated/vision/classification/resnet
Source SHA256: 4e8f8653e7a2222b3904cc3fe8e304cd8b339ce1d05fd24688162f86fb6df52c
License: Apache-2.0 (see LICENSE-ResNet.txt).

Build the binary with `python tools/prepare_local_vision.py` (onnx==1.17.0).
The script downloads and verifies the source; the application never downloads a
model or sends screen images. Include this folder and its ONNX file in releases.
ONNX Runtime is distributed under the MIT license (see LICENSE-OnnxRuntime.txt).

RGB input is aspect-preserving padded to 224x224 on a dark background, normalized
using ImageNet mean/std. This differs from classification center-cropping to avoid
cutting long equipment. Reference and query preprocessing are identical.

The UI is test-build-only. Select an equipment category, capture a tight icon area
or open an icon crop, then analyze. Captures stay in RAM unless Save Sample is
clicked. Sample images and labels are stored under AppData/HD2 Helper/local-vision.
The selected screen region lasts only for the diagnostic window's lifetime.
Prefer actual game icon samples over display assets, which can differ in angle,
background, padding, and appearance. Register different equipment labels for
meaningful comparisons. Samples are references, not neural-network fine-tuning.

Scores are cosine similarities, NOT accuracy probabilities. An experimental gate
(top score >= 0.80 and gap >= 0.05 across at least two labels) only labels a
candidate as clear; it never triggers keys or changes presets. Real game accuracy,
threshold calibration, menu-state detection and automatic reselection are NOT
validated or enabled. Compare against held-out game captures before integration.
CPU inference uses one thread with spinning disabled and runs only on demand.
