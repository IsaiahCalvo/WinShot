using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SD = System.Drawing;

namespace WinShot.Recording;

/// <summary>
/// Streaming GIF89a writer. Each frame is octree-quantized to a 256-color
/// local palette and LZW-compressed straight to the output stream, so memory
/// stays bounded no matter how long the recording runs. Not thread-safe:
/// feed it from a single encoder thread.
/// </summary>
public sealed class GifEncoder : IDisposable
{
    private const int MinCodeSize = 8;            // 256-color frames
    private const int ClearCode = 1 << MinCodeSize;
    private const int EndCode = ClearCode + 1;
    private const int MaxLzwCode = 4094;          // reset shy of the 4096 hard cap

    private readonly Stream _out;
    private readonly int _width;
    private readonly int _height;
    private readonly int _delayCentiseconds;
    private bool _finished;

    // Reused per-frame buffers so a long recording does not churn the GC.
    private byte[] _pixels = Array.Empty<byte>();
    private readonly byte[] _indices;
    private readonly byte[] _palette = new byte[256 * 3];
    private readonly Dictionary<int, long> _colorCounts = new();
    private readonly Dictionary<int, byte> _indexMap = new();
    private readonly Dictionary<int, int> _lzwDict = new(4096);

    // LZW bit packer state.
    private readonly byte[] _block = new byte[255];
    private int _blockLen;
    private int _bitBuffer;
    private int _bitCount;

    /// <summary>Takes ownership of <paramref name="output"/> and writes the GIF header immediately.</summary>
    public GifEncoder(Stream output, int width, int height, int fps)
    {
        if (width < 1 || width > ushort.MaxValue || height < 1 || height > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid GIF dimensions {width}×{height}.");

        _out = output;
        _width = width;
        _height = height;
        _indices = new byte[width * height];
        _delayCentiseconds = Math.Max(2, (int)Math.Round(100.0 / Math.Max(1, fps)));

        WriteHeader();
    }

    private void WriteHeader()
    {
        _out.Write(Encoding.ASCII.GetBytes("GIF89a"));

        // Logical screen descriptor: no global color table, 8-bit color resolution.
        WriteU16(_width);
        WriteU16(_height);
        _out.WriteByte(0x70);
        _out.WriteByte(0); // background color index
        _out.WriteByte(0); // pixel aspect ratio

        // NETSCAPE2.0 application extension: loop forever.
        _out.WriteByte(0x21);
        _out.WriteByte(0xFF);
        _out.WriteByte(0x0B);
        _out.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        _out.WriteByte(0x03);
        _out.WriteByte(0x01);
        WriteU16(0);       // 0 = infinite loop
        _out.WriteByte(0); // block terminator
    }

    public void AddFrame(SD.Bitmap frame)
    {
        if (_finished) throw new InvalidOperationException("Encoder already finished.");
        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException($"Frame is {frame.Width}×{frame.Height}, expected {_width}×{_height}.", nameof(frame));

        var data = frame.LockBits(new SD.Rectangle(0, 0, _width, _height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride;
        try
        {
            stride = data.Stride;
            int byteCount = stride * _height;
            if (_pixels.Length < byteCount)
                _pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, _pixels, 0, byteCount);
        }
        finally
        {
            frame.UnlockBits(data);
        }

        // Pass 1: histogram of distinct colors (screen content repeats heavily,
        // so this keeps the octree work proportional to distinct colors).
        _colorCounts.Clear();
        for (int y = 0; y < _height; y++)
        {
            int o = y * stride;
            for (int x = 0; x < _width; x++, o += 4)
            {
                int rgb = (_pixels[o + 2] << 16) | (_pixels[o + 1] << 8) | _pixels[o];
                ref long count = ref CollectionsMarshal.GetValueRefOrAddDefault(_colorCounts, rgb, out _);
                count++;
            }
        }

        // Quantize to at most 256 colors and map every distinct color to a palette index.
        var quantizer = new OctreeQuantizer(256);
        foreach (var kv in _colorCounts)
            quantizer.AddColor(kv.Key, kv.Value);
        Array.Clear(_palette, 0, _palette.Length);
        quantizer.BuildPalette(_palette);
        _indexMap.Clear();
        foreach (int rgb in _colorCounts.Keys)
            _indexMap[rgb] = (byte)quantizer.GetPaletteIndex(rgb);

        // Pass 2: index every pixel.
        int i = 0;
        for (int y = 0; y < _height; y++)
        {
            int o = y * stride;
            for (int x = 0; x < _width; x++, o += 4)
            {
                int rgb = (_pixels[o + 2] << 16) | (_pixels[o + 1] << 8) | _pixels[o];
                _indices[i++] = _indexMap[rgb];
            }
        }

        WriteGraphicControlExtension();
        WriteImageDescriptorAndPalette();
        WriteLzwImageData(_indices, _width * _height);
    }

    /// <summary>Writes the GIF trailer. Idempotent.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _out.WriteByte(0x3B);
        _out.Flush();
    }

    public void Dispose()
    {
        try { Finish(); }
        catch { /* stream may already be broken; Dispose must not throw */ }
        _out.Dispose();
    }

    private void WriteGraphicControlExtension()
    {
        _out.WriteByte(0x21);
        _out.WriteByte(0xF9);
        _out.WriteByte(0x04);
        _out.WriteByte(0x04); // disposal method 1 (do not dispose), no transparency
        WriteU16(_delayCentiseconds);
        _out.WriteByte(0);    // transparent color index (unused)
        _out.WriteByte(0);    // block terminator
    }

    private void WriteImageDescriptorAndPalette()
    {
        _out.WriteByte(0x2C);
        WriteU16(0);
        WriteU16(0);
        WriteU16(_width);
        WriteU16(_height);
        _out.WriteByte(0x87); // local color table, 256 entries, not interlaced
        _out.Write(_palette, 0, _palette.Length);
    }

    // ---- LZW (GIF variant: variable code size, LSB-first, 255-byte sub-blocks) ----

    private void WriteLzwImageData(byte[] indices, int count)
    {
        _out.WriteByte(MinCodeSize);
        _lzwDict.Clear();
        _blockLen = 0;
        _bitBuffer = 0;
        _bitCount = 0;
        int codeSize = MinCodeSize + 1;
        int nextCode = EndCode + 1;

        WriteCode(ClearCode, codeSize);
        int prefix = indices[0];
        for (int i = 1; i < count; i++)
        {
            int k = indices[i];
            int key = (prefix << 8) | k;
            if (_lzwDict.TryGetValue(key, out int found))
            {
                prefix = found;
                continue;
            }

            WriteCode(prefix, codeSize);
            _lzwDict[key] = nextCode++;
            // The decoder adds entries one emission behind the encoder, so it
            // bumps its code size one step later than our counter suggests:
            // grow at 2^codeSize + 1, not 2^codeSize (capped at 12 bits).
            if (nextCode == (1 << codeSize) + 1 && codeSize < 12)
                codeSize++;
            if (nextCode >= MaxLzwCode)
            {
                WriteCode(ClearCode, codeSize);
                _lzwDict.Clear();
                codeSize = MinCodeSize + 1;
                nextCode = EndCode + 1;
            }
            prefix = k;
        }

        WriteCode(prefix, codeSize);
        WriteCode(EndCode, codeSize);
        if (_bitCount > 0)
        {
            WriteDataByte((byte)_bitBuffer);
            _bitBuffer = 0;
            _bitCount = 0;
        }
        FlushBlock();
        _out.WriteByte(0); // image data terminator
    }

    private void WriteCode(int code, int bits)
    {
        _bitBuffer |= code << _bitCount;
        _bitCount += bits;
        while (_bitCount >= 8)
        {
            WriteDataByte((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    private void WriteDataByte(byte b)
    {
        _block[_blockLen++] = b;
        if (_blockLen == 255)
            FlushBlock();
    }

    private void FlushBlock()
    {
        if (_blockLen == 0) return;
        _out.WriteByte((byte)_blockLen);
        _out.Write(_block, 0, _blockLen);
        _blockLen = 0;
    }

    private void WriteU16(int value)
    {
        _out.WriteByte((byte)(value & 0xFF));
        _out.WriteByte((byte)((value >> 8) & 0xFF));
    }

    // ---- Octree color quantization ----

    private sealed class OctreeNode
    {
        public OctreeNode?[]? Children; // null for leaves
        public bool IsLeaf;
        public long R, G, B, Count;
        public int PaletteIndex;
    }

    /// <summary>
    /// Octree quantizer with a bounded tree: colors descend five levels
    /// (rgb555 paths, at most ~37k nodes regardless of input) while the color
    /// sums keep full 8-bit precision. Reduction happens once at palette-build
    /// time, merging the least-populated branches bottom-up, which avoids the
    /// quality collapse of reduce-while-adding on high-color frames.
    /// </summary>
    private sealed class OctreeQuantizer
    {
        private const int Depth = 5;

        private readonly OctreeNode _root = new() { Children = new OctreeNode?[8] };
        private readonly List<OctreeNode>[] _levels; // internal nodes by level; leaves live at Depth
        private readonly int _maxColors;
        private int _leafCount;

        public OctreeQuantizer(int maxColors)
        {
            _maxColors = maxColors;
            _levels = new List<OctreeNode>[Depth];
            for (int i = 0; i < _levels.Length; i++)
                _levels[i] = new List<OctreeNode>();
            _levels[0].Add(_root);
        }

        public void AddColor(int rgb, long weight)
        {
            int r = (rgb >> 16) & 0xFF;
            int g = (rgb >> 8) & 0xFF;
            int b = rgb & 0xFF;

            var node = _root;
            node.Count += weight; // Count is the subtree weight, used to merge sparse branches first
            for (int level = 0; level < Depth; level++)
            {
                int shift = 7 - level;
                int idx = (((r >> shift) & 1) << 2) | (((g >> shift) & 1) << 1) | ((b >> shift) & 1);
                var child = node.Children![idx];
                if (child is null)
                {
                    child = new OctreeNode();
                    if (level == Depth - 1)
                    {
                        child.IsLeaf = true;
                        _leafCount++;
                    }
                    else
                    {
                        child.Children = new OctreeNode?[8];
                        _levels[level + 1].Add(child);
                    }
                    node.Children[idx] = child;
                }
                child.Count += weight;
                node = child;
            }

            node.R += r * weight;
            node.G += g * weight;
            node.B += b * weight;
        }

        /// <summary>
        /// Reduces to at most the palette size, fills RGB triplets into
        /// <paramref name="palette768"/>, and returns the color count.
        /// Call once, before <see cref="GetPaletteIndex"/>.
        /// </summary>
        public int BuildPalette(byte[] palette768)
        {
            // Bottom-up: a level is only processed once everything below it is
            // a leaf, so every merge folds leaf children into their parent.
            for (int level = Depth - 1; level >= 0 && _leafCount > _maxColors; level--)
            {
                var list = _levels[level];
                list.Sort(static (a, b) => a.Count.CompareTo(b.Count));
                foreach (var node in list)
                {
                    if (_leafCount <= _maxColors) break;
                    Merge(node);
                }
            }

            int index = 0;
            Walk(_root, palette768, ref index);
            return index;
        }

        private void Merge(OctreeNode node)
        {
            if (node.IsLeaf) return;
            int mergedChildren = 0;
            for (int i = 0; i < 8; i++)
            {
                var child = node.Children![i];
                if (child is null) continue;
                node.R += child.R;
                node.G += child.G;
                node.B += child.B; // Count is already the subtree total
                node.Children[i] = null;
                mergedChildren++;
            }
            node.Children = null;
            node.IsLeaf = true;
            _leafCount += 1 - mergedChildren;
        }

        private static void Walk(OctreeNode node, byte[] palette, ref int index)
        {
            if (node.IsLeaf)
            {
                long c = Math.Max(1, node.Count);
                node.PaletteIndex = index;
                palette[index * 3] = (byte)(node.R / c);
                palette[index * 3 + 1] = (byte)(node.G / c);
                palette[index * 3 + 2] = (byte)(node.B / c);
                index++;
                return;
            }
            foreach (var child in node.Children!)
            {
                if (child is not null)
                    Walk(child, palette, ref index);
            }
        }

        public int GetPaletteIndex(int rgb)
        {
            int r = (rgb >> 16) & 0xFF;
            int g = (rgb >> 8) & 0xFF;
            int b = rgb & 0xFF;

            var node = _root;
            for (int level = 0; !node.IsLeaf; level++)
            {
                int shift = 7 - level;
                int idx = (((r >> shift) & 1) << 2) | (((g >> shift) & 1) << 1) | ((b >> shift) & 1);
                var child = node.Children![idx];
                if (child is null)
                {
                    // Only reachable for colors that were never added; fall back
                    // to any populated branch instead of crashing.
                    child = Array.Find(node.Children, c => c is not null)
                        ?? throw new InvalidOperationException("Octree palette lookup failed.");
                }
                node = child;
            }
            return node.PaletteIndex;
        }
    }
}
