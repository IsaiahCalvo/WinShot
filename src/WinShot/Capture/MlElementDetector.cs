using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using WinShot.Core;
using SD = System.Drawing;

namespace WinShot.Capture;

/// <summary>
/// Interactable-element detection from PIXELS, via Microsoft's OmniParser icon-detect model
/// (YOLOv8, ONNX). This is the tier that works on every app equally — Electron included —
/// because it looks at the screenshot the way a person does instead of asking APIs that
/// Chromium apps answer slowly or not at all.
///
/// Measured on this machine: ~42 ms inference on CPU for one window, 78 elements found on a
/// live desktop (buttons, tabs, menu rows) with sensible boxes. One scan per window per
/// overlay session, run on a worker against the FROZEN snapshot, so the cost is paid once and
/// never against the glide.
///
/// The model finds INTERACTABLES (it was trained on clickable regions), so its boxes are
/// buttons/fields/rows — complementary to VisualElementDetector's bigger content blocks and
/// the HWND tier's panes. Model: onnx-community/OmniParser-icon_detect_640x640 (AGPL-3.0,
/// YOLO-derived — same license family as the GPL app).
/// </summary>
internal static class MlElementDetector
{
    private const int InputSize = 640;
    private const float ConfidenceFloor = 0.20f; // below this YOLO is guessing
    private const float NmsIou = 0.45f;          // overlap above this = same element twice
    private const int MaxBoxes = 160;            // a window rarely has more real controls

    private static readonly object Gate = new();
    private static InferenceSession? _session;
    private static bool _loadFailed;

    /// <summary>Boxes for the window's interactable elements, in the bitmap's own coordinate
    /// space. Empty on any failure — the caller has other tiers. Blocking (~50-100 ms); call
    /// from a worker.</summary>
    public static List<SD.Rectangle> Detect(SD.Bitmap frozen, SD.Rectangle windowRect)
    {
        try
        {
            InferenceSession? session = GetSession();
            if (session is null)
                return new List<SD.Rectangle>();

            var area = SD.Rectangle.Intersect(windowRect, new SD.Rectangle(0, 0, frozen.Width, frozen.Height));
            if (area.Width < 64 || area.Height < 64)
                return new List<SD.Rectangle>();

            float scale = Math.Min((float)InputSize / area.Width, (float)InputSize / area.Height);
            int scaledW = Math.Max(1, (int)(area.Width * scale));
            int scaledH = Math.Max(1, (int)(area.Height * scale));

            DenseTensor<float> input = Letterbox(frozen, area, scaledW, scaledH);

            using var results = session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("images", input),
            });
            // Output: (1, 5, 8400) — cx, cy, w, h, confidence per anchor.
            var output = (DenseTensor<float>)results.First().Value;

            var candidates = new List<(SD.RectangleF Box, float Conf)>();
            int anchors = output.Dimensions[2];
            for (int i = 0; i < anchors; i++)
            {
                float conf = output[0, 4, i];
                if (conf < ConfidenceFloor)
                    continue;
                float cx = output[0, 0, i], cy = output[0, 1, i];
                float w = output[0, 2, i], h = output[0, 3, i];
                candidates.Add((new SD.RectangleF(cx - (w / 2), cy - (h / 2), w, h), conf));
            }

            candidates.Sort((a, b) => b.Conf.CompareTo(a.Conf));
            var kept = Nms(candidates);

            var boxes = new List<SD.Rectangle>(kept.Count);
            foreach (var (box, _) in kept)
            {
                var mapped = new SD.Rectangle(
                    area.X + (int)(box.X / scale),
                    area.Y + (int)(box.Y / scale),
                    (int)(box.Width / scale),
                    (int)(box.Height / scale));
                mapped.Intersect(area);
                if (mapped.Width >= 8 && mapped.Height >= 8)
                    boxes.Add(mapped);
            }
            return boxes;
        }
        catch (Exception ex)
        {
            Log.Error("ML element detection failed (non-fatal)", ex);
            return new List<SD.Rectangle>();
        }
    }

    private static InferenceSession? GetSession()
    {
        lock (Gate)
        {
            if (_session is not null || _loadFailed)
                return _session;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "ml", "omniparser-icon-detect.onnx");
                var options = new SessionOptions
                {
                    // The selector must stay responsive: cap the model's parallelism rather
                    // than let it grab every core mid-hover.
                    IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2),
                };
                _session = new InferenceSession(path, options);
            }
            catch (Exception ex)
            {
                Log.Error("ONNX model load failed; ML tier disabled for this run", ex);
                _loadFailed = true;
            }
            return _session;
        }
    }

    private static List<(SD.RectangleF Box, float Conf)> Nms(List<(SD.RectangleF Box, float Conf)> sorted)
    {
        var kept = new List<(SD.RectangleF, float)>();
        foreach (var candidate in sorted)
        {
            if (kept.Count >= MaxBoxes)
                break;
            bool duplicate = false;
            foreach (var (existing, _) in kept)
            {
                if (Iou(candidate.Box, existing) > NmsIou)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                kept.Add(candidate);
        }
        return kept;
    }

    private static float Iou(SD.RectangleF a, SD.RectangleF b)
    {
        var intersection = SD.RectangleF.Intersect(a, b);
        if (intersection.IsEmpty)
            return 0;
        float overlap = intersection.Width * intersection.Height;
        return overlap / ((a.Width * a.Height) + (b.Width * b.Height) - overlap);
    }

    /// <summary>The window crop scaled into the top-left of a 640x640 gray canvas, RGB CHW,
    /// 0..1 — YOLO letterbox preprocessing, minus centering (top-left keeps the unmap math
    /// to one multiply).</summary>
    private static DenseTensor<float> Letterbox(SD.Bitmap frozen, SD.Rectangle area, int scaledW, int scaledH)
    {
        using var canvas = new SD.Bitmap(InputSize, InputSize, PixelFormat.Format24bppRgb);
        using (var g = SD.Graphics.FromImage(canvas))
        {
            g.Clear(SD.Color.FromArgb(114, 114, 114));
            g.InterpolationMode = SD.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(frozen, new SD.Rectangle(0, 0, scaledW, scaledH), area, SD.GraphicsUnit.Pixel);
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        var data = canvas.LockBits(new SD.Rectangle(0, 0, InputSize, InputSize),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < InputSize; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < InputSize; x++)
                {
                    int i = x * 3;
                    tensor[0, 0, y, x] = row[i + 2] / 255f; // R (bitmap rows are BGR)
                    tensor[0, 1, y, x] = row[i + 1] / 255f; // G
                    tensor[0, 2, y, x] = row[i] / 255f;     // B
                }
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }
        return tensor;
    }
}
