using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HD2_Helper;

internal sealed record VisionItem(string Type, string Name, string ImagePath);
internal sealed record VisionCandidate(string Name, float Similarity, string Source);
internal sealed record VisionResult(IReadOnlyList<VisionCandidate> Candidates, bool Uncertain, int ReferenceCount);

// A pretrained visual descriptor with reference retrieval, not a game-trained classifier.
internal sealed class LocalVisionRecognizer : IDisposable
{
    private readonly string _sampleRoot;
    private readonly string _modelPath;
    private readonly Dictionary<string, float[]> _features = new(StringComparer.OrdinalIgnoreCase);
    private InferenceSession? _session;
    public IReadOnlyList<VisionItem> Items { get; }

    public LocalVisionRecognizer(string root, string appData)
    {
        _modelPath = Path.Combine(root, "models", "resnet18-features.onnx");
        _sampleRoot = Path.Combine(appData, "local-vision", "samples");
        using var db = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "database.json")));
        var items = new List<VisionItem>();
        foreach (var type in db.RootElement.EnumerateObject())
        {
            string? folder = type.Name switch {
                "스트라타젬" => "Stratagems", "방어구" => "Armors",
                "주 무기" or "보조 무기" or "투척 무기" => "Weapons", _ => null
            };
            if (folder == null) continue;
            foreach (var category in type.Value.EnumerateObject())
            {
                if (category.Name == "패시브") continue;
                foreach (var item in category.Value.EnumerateArray())
                {
                    string name = item.GetProperty("Name").GetString()!;
                    items.Add(new(type.Name, name, Path.Combine(root, "images", folder, name + ".png")));
                }
            }
        }
        Items = items.DistinctBy(item => (item.Type, item.Name)).ToList();
    }

    private InferenceSession Session
    {
        get
        {
            if (_session != null) return _session;
            if (!File.Exists(_modelPath))
                throw new FileNotFoundException("로컬 모델이 없습니다. models 폴더가 포함된 테스트 패키지를 설치해 주세요.", _modelPath);
            using var options = new SessionOptions {
                IntraOpNumThreads = 1, InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            _session = new InferenceSession(_modelPath, options);
            return _session;
        }
    }

    public float[] Embed(Bitmap image)
    {
        var session = Session;
        using var normalized = new Bitmap(224, 224, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(normalized))
        {
            // Fit rather than center-crop: long weapons must retain the stock and barrel.
            g.Clear(Color.FromArgb(33, 33, 33));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = 224f / Math.Max(image.Width, image.Height);
            float w = image.Width * scale, h = image.Height * scale;
            g.DrawImage(image, (224 - w) / 2, (224 - h) / 2, w, h);
        }
        var tensor = new DenseTensor<float>(new[] { 1, 3, 224, 224 });
        var bits = normalized.LockBits(new Rectangle(0, 0, 224, 224), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[bits.Stride * 224];
            Marshal.Copy(bits.Scan0, bytes, 0, bytes.Length);
            float[] mean = { .485f, .456f, .406f }, std = { .229f, .224f, .225f };
            for (int y = 0; y < 224; y++)
                for (int x = 0; x < 224; x++)
                    for (int c = 0; c < 3; c++)
                        tensor[0, c, y, x] = (bytes[y * bits.Stride + x * 3 + 2 - c] / 255f - mean[c]) / std[c];
        }
        finally { normalized.UnlockBits(bits); }
        using var output = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.Single(), tensor) });
        var vector = output.First().AsEnumerable<float>().ToArray();
        double norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (vector.Length != 512 || norm < 1e-8 || !double.IsFinite(norm))
            throw new InvalidDataException("로컬 모델의 특징 출력이 올바르지 않습니다.");
        for (int i = 0; i < vector.Length; i++) vector[i] /= (float)norm;
        return vector;
    }

    public VisionResult Recognize(Bitmap image, string type, bool samplesOnly, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasVisualContent(image)) return new(Array.Empty<VisionCandidate>(), true, 0);
        float[] query = Embed(image);
        var matches = new List<VisionCandidate>();
        int count = 0;
        foreach (var item in Items.Where(i => i.Type == type))
        {
            var paths = new List<(string Path, string Source)>();
            if (!samplesOnly && File.Exists(item.ImagePath)) paths.Add((item.ImagePath, "기본 이미지"));
            string dir = SampleDirectory(item);
            if (Directory.Exists(dir))
                paths.AddRange(Directory.EnumerateFiles(dir, "*.png").Select(p => (p, "등록 샘플")));
            foreach (var (path, source) in paths)
            {
                token.ThrowIfCancellationRequested();
                if (!_features.TryGetValue(path, out var vector))
                {
                    using var reference = new Bitmap(path);
                    vector = Embed(reference);
                    _features[path] = vector;
                }
                float similarity = 0;
                for (int i = 0; i < query.Length; i++) similarity += query[i] * vector[i];
                matches.Add(new(item.Name, Math.Clamp(similarity, -1, 1), source));
                count++;
            }
        }
        return Rank(matches, count);
    }

    private static bool HasVisualContent(Bitmap image)
    {
        double sum = 0, squares = 0;
        int count = 0;
        for (int y = 0; y < image.Height; y += Math.Max(1, image.Height / 64))
            for (int x = 0; x < image.Width; x += Math.Max(1, image.Width / 64))
            {
                Color c = image.GetPixel(x, y);
                double value = (c.R + c.G + c.B) / 3.0 * c.A / 255.0;
                sum += value;
                squares += value * value;
                count++;
            }
        return squares / count - Math.Pow(sum / count, 2) >= 9;
    }

    internal static VisionResult Rank(IEnumerable<VisionCandidate> matches, int count)
    {
        var ranked = matches.GroupBy(m => m.Name).Select(g => g.OrderByDescending(m => m.Similarity).First())
            .OrderByDescending(m => m.Similarity).Take(5).ToList();
        // Conservative experimental gates; cosine is not a calibrated probability.
        bool uncertain = ranked.Count < 2 || ranked[0].Similarity < .80f || ranked[0].Similarity - ranked[1].Similarity < .05f;
        return new(ranked, uncertain, count);
    }

    private string SampleDirectory(VisionItem item) => Path.Combine(_sampleRoot,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Type + "\n" + StratagemNames.StorageName(item.Name)))));

    public void SaveSample(Bitmap image, VisionItem item)
    {
        if (!Items.Contains(item)) throw new ArgumentException("등록할 장비가 올바르지 않습니다.");
        string dir = SampleDirectory(item);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
        image.Save(path, ImageFormat.Png);
        try { File.WriteAllText(Path.Combine(dir, "label.json"), JsonSerializer.Serialize(new { item.Type, item.Name })); }
        catch { File.Delete(path); throw; }
    }

    public int DeleteSamples(VisionItem item)
    {
        if (!Items.Contains(item)) throw new ArgumentException("장비가 올바르지 않습니다.");
        string dir = SampleDirectory(item);
        if (!Directory.Exists(dir)) return 0;
        int removed = 0;
        foreach (string path in Directory.EnumerateFiles(dir, "*.png"))
        {
            File.Delete(path);
            _features.Remove(path);
            removed++;
        }
        return removed;
    }

    public void Dispose() { _session?.Dispose(); _features.Clear(); }
}
