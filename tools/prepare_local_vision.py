"""Build-only dependency: onnx==1.17.0. No network access at application runtime."""
import hashlib
import pathlib
import tempfile
import urllib.request
import onnx

URL = "https://media.githubusercontent.com/media/onnx/models/main/validated/vision/classification/resnet/model/resnet18-v1-7.onnx"
SHA256 = "4e8f8653e7a2222b3904cc3fe8e304cd8b339ce1d05fd24688162f86fb6df52c"
source = pathlib.Path(tempfile.gettempdir()) / "hd2-resnet18-v1-7.onnx"
if not source.exists():
    urllib.request.urlretrieve(URL, source)
if hashlib.sha256(source.read_bytes()).hexdigest() != SHA256:
    raise RuntimeError("ResNet18 source checksum mismatch")
model = onnx.load(source)
model.ir_version = 7
weights = {tensor.name for tensor in model.graph.initializer}
inputs = [value for value in model.graph.input if value.name not in weights]
del model.graph.input[:]
model.graph.input.extend(inputs)
pool = next(node for node in model.graph.node if node.op_type == "GlobalAveragePool")
target = pathlib.Path(__file__).resolve().parents[1] / "models" / "resnet18-features.onnx"
target.parent.mkdir(exist_ok=True)
# Remove the ImageNet classifier; keep the pooled 512-dimensional visual descriptor.
with tempfile.TemporaryDirectory() as tmp:
    modern = pathlib.Path(tmp) / "source.onnx"
    onnx.save(model, modern)
    onnx.utils.extract_model(str(modern), str(target), [model.graph.input[0].name], [pool.output[0]])
onnx.checker.check_model(str(target))
print(f"Created {target}: sha256={hashlib.sha256(target.read_bytes()).hexdigest()}")
