/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using ImageGlass.SDK.Plugins;
using System.Runtime.InteropServices;
using Vortice.Win32;
using Vortice.Win32.Com;
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

/// <summary>
/// Reusable per-call buffers for painting one GIF frame.
/// </summary>
internal sealed class GifScratch
{
    public readonly uint[] Colors = new uint[256];
    public byte[] Pixels = [];

    public byte[] Rent(int size)
    {
        if (Pixels.Length < size) Pixels = new byte[size];
        return Pixels;
    }
}


/// <summary>
/// Plays one file with its decoder (and GIF canvas) kept between frames; owned by the <see cref="AnimWorker"/> thread, so unsynchronized.
/// </summary>
internal sealed unsafe class AnimPlayer : IDisposable
{
    public string Path { get; }
    public AnimTimeline Timeline { get; }

    private IWICImagingFactory* _factory;
    private IWICStream* _stream;
    private IWICBitmapDecoder* _decoder;

    // The decoder reads from this copy, so the file stays free to delete or rename while it plays.
    private byte* _file;

    private byte[]? _canvas;
    private int _composed = -1;
    private byte[]? _saved;
    private readonly GifScratch _scratch = new();


    private AnimPlayer(string path, AnimTimeline timeline)
    {
        Path = path;
        Timeline = timeline;
    }


    public static AnimPlayer? Open(string path, AnimTimeline timeline, out IGStatus status)
    {
        var player = new AnimPlayer(path, timeline);
        status = player.OpenCore();
        if (status == IGStatus.OK) return player;

        player.Dispose();
        return null;
    }


    private IGStatus OpenCore()
    {
        int length;
        try
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > int.MaxValue) return IGStatus.IoError;

            length = (int)file.Length;
            _file = (byte*)NativeMemory.Alloc((nuint)length);
            file.ReadExactly(new Span<byte>(_file, length));
        }
        catch (IOException) { return IGStatus.IoError; }
        catch (UnauthorizedAccessException) { return IGStatus.IoError; }

        _factory = WicFactory.Create();
        if (_factory == null) return IGStatus.Internal;

        IWICStream* stream = null;
        if (_factory->CreateStream(&stream).Failure) return IGStatus.Internal;
        _stream = stream;
        if (_stream->InitializeFromMemory(_file, (uint)length).Failure) return IGStatus.Internal;

        IWICBitmapDecoder* decoder = null;
        if (_factory->CreateDecoderFromStream((IStream*)_stream, null, WIC.WICDecodeMetadataCacheOnDemand, &decoder).Failure)
        {
            return IGStatus.DecodeFailed;
        }
        _decoder = decoder;

        // The timeline came from another read of the file; a mismatch means it changed in between.
        uint frameCount = 0;
        _decoder->GetFrameCount(&frameCount);
        return frameCount == Timeline.FrameCount ? IGStatus.OK : IGStatus.DecodeFailed;
    }


    /// <summary>
    /// Produces the fully composed frame <paramref name="index"/> in a buffer the host owns.
    /// </summary>
    public IGStatus Render(int index, IGPixelBuffer* outBuf, void* cancellation)
    {
        if (index < 0 || index >= Timeline.FrameCount) return IGStatus.InvalidArg;

        var t = Timeline;
        _canvas ??= new byte[t.Width * t.Height * 4];

        // Replay as little as possible: carry on from the frame on the canvas, or restart at a keyframe.
        var start = _composed >= 0 && _composed <= index ? _composed + 1 : -1;
        for (var k = index; k >= Math.Max(start, 0); k--)
        {
            if (!t.IsGifKeyframe(k)) continue;

            start = k;
            Array.Clear(_canvas);
            _composed = -1;
            break;
        }

        for (var k = start; k <= index; k++)
        {
            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            if (_composed >= 0) ApplyDisposal(t.GifFrames[_composed]);
            if (t.GifFrames[k].Disposal == AnimTimeline.DISPOSE_PREVIOUS) Save(t.GifFrames[k]);

            var status = DrawGifFrame(_factory, _decoder, t, k, _canvas, _scratch);
            if (status != IGStatus.OK)
            {
                // The canvas is half painted; the next request must start over.
                _composed = -1;
                return status;
            }
            _composed = k;
        }

        return Publish(_canvas, t.Width, t.Height, outBuf);
    }


    /// <summary>Applies the disposal of the frame on the canvas before the next one is painted.</summary>
    private void ApplyDisposal(in GifFrame frame)
    {
        if (frame.Disposal == AnimTimeline.DISPOSE_BACKGROUND)
        {
            // Restores to transparent, not the background color index, like every current browser.
            CopyRect(frame, null, toCanvas: true);
        }
        else if (frame.Disposal == AnimTimeline.DISPOSE_PREVIOUS && _saved is not null)
        {
            CopyRect(frame, _saved, toCanvas: true);
        }
    }


    private void Save(in GifFrame frame)
    {
        var (_, _, w, h) = Clip(frame);
        var size = w * h * 4;
        if (_saved is null || _saved.Length < size) _saved = new byte[Math.Max(size, 4)];
        CopyRect(frame, _saved, toCanvas: false);
    }


    /// <summary>Copies the frame's clipped rect between the canvas and a packed buffer; a null buffer clears it.</summary>
    private void CopyRect(in GifFrame frame, byte[]? packed, bool toCanvas)
    {
        var (x, y, w, h) = Clip(frame);
        var canvasStride = Timeline.Width * 4;
        var rowBytes = w * 4;

        for (var row = 0; row < h; row++)
        {
            var canvasRow = _canvas.AsSpan((y + row) * canvasStride + x * 4, rowBytes);
            if (packed is null) canvasRow.Clear();
            else if (toCanvas) packed.AsSpan(row * rowBytes, rowBytes).CopyTo(canvasRow);
            else canvasRow.CopyTo(packed.AsSpan(row * rowBytes, rowBytes));
        }
    }


    private (int X, int Y, int W, int H) Clip(in GifFrame frame)
    {
        var x0 = Math.Clamp(frame.Left, 0, Timeline.Width);
        var y0 = Math.Clamp(frame.Top, 0, Timeline.Height);
        var x1 = Math.Clamp(frame.Left + frame.Width, 0, Timeline.Width);
        var y1 = Math.Clamp(frame.Top + frame.Height, 0, Timeline.Height);
        return (x0, y0, x1 - x0, y1 - y0);
    }


    /// <summary>
    /// Paints GIF frame <paramref name="index"/> over <paramref name="canvas"/> at its offset, skipping transparent pixels.
    /// </summary>
    public static IGStatus DrawGifFrame(IWICImagingFactory* factory, IWICBitmapDecoder* decoder,
        AnimTimeline timeline, int index, byte[] canvas, GifScratch scratch)
    {
        IWICBitmapFrameDecode* frame = null;
        IWICPalette* palette = null;
        IWICBitmapSource* converted = null;

        try
        {
            if (decoder->GetFrame((uint)index, &frame).Failure) return IGStatus.DecodeFailed;

            uint width = 0, height = 0;
            if (frame->GetSize(&width, &height).Failure) return IGStatus.DecodeFailed;
            if (width == 0 || height == 0) return IGStatus.OK;

            Guid format;
            if (frame->GetPixelFormat(&format).Failure) format = default;

            var info = timeline.GifFrames[index];
            var pixelCount = (long)width * height;

            if (format == WIC.GUID_WICPixelFormat8bppIndexed && pixelCount <= int.MaxValue)
            {
                if (factory->CreatePalette(&palette).Failure) return IGStatus.Internal;
                if (frame->CopyPalette(palette).Failure && decoder->CopyPalette(palette).Failure)
                {
                    return IGStatus.DecodeFailed;
                }

                var colors = scratch.Colors;
                uint count = 0;
                fixed (uint* pColors = colors)
                {
                    if (palette->GetColors(256, pColors, &count).Failure) return IGStatus.DecodeFailed;
                }

                // Indices past the table read as opaque black, as giflib and Firefox do.
                colors.AsSpan((int)Math.Min(count, 256u)).Fill(0xFF000000);
                if (info.Transparent) colors[info.TransparentIndex] = 0;

                var indices = scratch.Rent((int)pixelCount);
                fixed (byte* pIndices = indices)
                {
                    if (frame->CopyPixels(null, width, (uint)pixelCount, pIndices).Failure) return IGStatus.DecodeFailed;
                }

                Composite(timeline, info.Left, info.Top, (int)width, (int)height, indices, colors, canvas);
                return IGStatus.OK;
            }

            // Not the usual palette frame: let WIC convert it and blend the result.
            var stride = (long)width * 4;
            if (stride * height > int.MaxValue) return IGStatus.OutOfMemory;

            var target = WIC.GUID_WICPixelFormat32bppBGRA;
            if (WIC.WICConvertBitmapSource(&target, (IWICBitmapSource*)frame, &converted).Failure) return IGStatus.Unsupported;

            var pixels = scratch.Rent((int)(stride * height));
            fixed (byte* pPixels = pixels)
            {
                if (converted->CopyPixels(null, (uint)stride, (uint)(stride * height), pPixels).Failure)
                {
                    return IGStatus.DecodeFailed;
                }
            }

            Composite(timeline, info.Left, info.Top, (int)width, (int)height, pixels, null, canvas);
            return IGStatus.OK;
        }
        finally
        {
            ComInterop.Release(ref converted);
            ComInterop.Release(ref palette);
            ComInterop.Release(ref frame);
        }
    }


    /// <summary>
    /// Blends a frame onto the canvas, clipped to it. With <paramref name="colors"/> the source is palette indices, else BGRA.
    /// </summary>
    private static void Composite(AnimTimeline timeline, int left, int top, int width, int height,
        byte[] source, uint[]? colors, byte[] canvas)
    {
        var canvasWidth = timeline.Width;
        var x0 = Math.Max(0, left);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(canvasWidth, left + width);
        var y1 = Math.Min(timeline.Height, top + height);
        if (x1 <= x0 || y1 <= y0) return;

        fixed (byte* pSource = source)
        fixed (byte* pCanvas = canvas)
        {
            var dst = (uint*)pCanvas;
            for (var y = y0; y < y1; y++)
            {
                var dstRow = dst + (long)y * canvasWidth;
                var srcOffset = (long)(y - top) * width - left;

                for (var x = x0; x < x1; x++)
                {
                    var c = colors is not null ? colors[pSource[srcOffset + x]] : ((uint*)pSource)[srcOffset + x];
                    var alpha = c >> 24;
                    if (alpha == 0xFF) dstRow[x] = c;
                    else if (alpha != 0) dstRow[x] = BlendOver(c, dstRow[x]);
                }
            }
        }
    }


    /// <summary>Source-over for straight (non-premultiplied) BGRA.</summary>
    private static uint BlendOver(uint src, uint dst)
    {
        uint sa = src >> 24, da = dst >> 24;
        var outA = sa + da * (255 - sa) / 255;
        if (outA == 0) return 0;

        uint Channel(int shift)
        {
            uint s = (src >> shift) & 0xFF, d = (dst >> shift) & 0xFF;
            return (s * sa + d * da * (255 - sa) / 255) / outA;
        }

        return (outA << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }


    /// <summary>Copies a composed canvas into a buffer the host owns.</summary>
    public static IGStatus Publish(byte[] canvas, int width, int height, IGPixelBuffer* outBuf)
    {
        var byteCount = (long)width * height * 4;
        var pixels = NativeBuffers.AllocPixels((nuint)byteCount);
        if (pixels == null) return IGStatus.OutOfMemory;

        canvas.AsSpan(0, (int)byteCount).CopyTo(new Span<byte>(pixels, (int)byteCount));
        outBuf->Data = pixels;
        outBuf->Width = width;
        outBuf->Height = height;
        outBuf->Stride = width * 4;
        outBuf->PixelFormat = (int)IGPixelFormat.Bgra8Unorm;
        outBuf->ReleaseContext = pixels;
        return IGStatus.OK;
    }


    public void Dispose()
    {
        // The decoder reads from the stream, which reads from _file: release in that order.
        ComInterop.Release(ref _decoder);
        ComInterop.Release(ref _stream);
        ComInterop.Release(ref _factory);

        if (_file != null)
        {
            NativeMemory.Free(_file);
            _file = null;
        }

        _canvas = null;
        _saved = null;
        _composed = -1;
    }
}
