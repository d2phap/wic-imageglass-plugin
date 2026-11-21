/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

internal enum AnimKind { Gif, Webp }


/// <summary>
/// Where one GIF frame lands on the logical screen and what happens to its rect afterwards.
/// </summary>
internal readonly record struct GifFrame(int Left, int Top, int Width, int Height, int Disposal,
    bool Transparent, byte TransparentIndex);


/// <summary>
/// One file version's immutable animation shape (canvas, timing, GIF placement), shared across threads.
/// </summary>
internal sealed unsafe class AnimTimeline
{
    public const int DISPOSE_BACKGROUND = 2;
    public const int DISPOSE_PREVIOUS = 3;

    public required AnimKind Kind { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Full play-throughs, 0 = forever: the <c>IGAnimationInfo.LoopCount</c> convention.</summary>
    public required int LoopCount { get; init; }

    public required int[] DurationsMs { get; init; }
    public required bool[] FrameHasAlpha { get; init; }
    public GifFrame[] GifFrames { get; init; } = [];

    public int FrameCount => DurationsMs.Length;
    public bool HasAlpha => Array.IndexOf(FrameHasAlpha, true) >= 0;


    public bool CoversCanvas(in GifFrame f)
        => f.Left <= 0 && f.Top <= 0 && f.Left + f.Width >= Width && f.Top + f.Height >= Height;


    /// <summary>
    /// Whether the canvas after GIF frame <paramref name="index"/> does not depend on any earlier frame.
    /// </summary>
    public bool IsGifKeyframe(int index)
    {
        if (index <= 0) return true;

        // A restore-to-previous frame hands back the canvas from before it, so it is never a fresh start.
        var frame = GifFrames[index];
        if (!frame.Transparent && frame.Disposal != DISPOSE_PREVIOUS && CoversCanvas(frame)) return true;

        var previous = GifFrames[index - 1];
        return previous.Disposal == DISPOSE_BACKGROUND && CoversCanvas(previous);
    }


    /// <summary>
    /// Reads the timeline of an opened GIF or WebP decoder; <c>null</c> for any other container.
    /// </summary>
    public static AnimTimeline? Read(IWICBitmapDecoder* decoder, Guid container, string path)
    {
        uint frameCount = 0;
        if (decoder->GetFrameCount(&frameCount).Failure || frameCount == 0) return null;

        if (container == WIC.GUID_ContainerFormatGif) return ReadGif(decoder, (int)frameCount);
        if (container == WIC.GUID_ContainerFormatWebp) return ReadWebp(decoder, (int)frameCount, path);
        return null;
    }


    private static AnimTimeline ReadGif(IWICBitmapDecoder* decoder, int frameCount)
    {
        var frames = new GifFrame[frameCount];
        var durations = new int[frameCount];
        uint screenWidth = 0, screenHeight = 0;
        var loopCount = 1;

        IWICMetadataQueryReader* global = null;
        try
        {
            if (decoder->GetMetadataQueryReader(&global).Success)
            {
                WicMetadata.TryGetUInt(global, "/logscrdesc/Width", out screenWidth);
                WicMetadata.TryGetUInt(global, "/logscrdesc/Height", out screenHeight);
                loopCount = ParseGifLoopCount(WicMetadata.GetBytes(global, "/appext/Application"),
                    WicMetadata.GetBytes(global, "/appext/Data"));
            }
        }
        finally
        {
            ComInterop.Release(ref global);
        }

        for (var i = 0; i < frameCount; i++)
        {
            IWICBitmapFrameDecode* frame = null;
            IWICMetadataQueryReader* reader = null;
            try
            {
                uint width = 0, height = 0, left = 0, top = 0, disposal = 0, delay = 0, transparent = 0, transparentIndex = 0;
                if (decoder->GetFrame((uint)i, &frame).Success)
                {
                    frame->GetSize(&width, &height);
                    if (frame->GetMetadataQueryReader(&reader).Success)
                    {
                        WicMetadata.TryGetUInt(reader, "/imgdesc/Left", out left);
                        WicMetadata.TryGetUInt(reader, "/imgdesc/Top", out top);
                        WicMetadata.TryGetUInt(reader, "/grctlext/Disposal", out disposal);
                        WicMetadata.TryGetUInt(reader, "/grctlext/Delay", out delay);
                        WicMetadata.TryGetUInt(reader, "/grctlext/TransparencyFlag", out transparent);
                        WicMetadata.TryGetUInt(reader, "/grctlext/TransparentColorIndex", out transparentIndex);
                    }
                }

                frames[i] = new GifFrame((int)left, (int)top, (int)width, (int)height, (int)disposal,
                    transparent != 0, (byte)transparentIndex);

                // Hundredths of a second; the host normalizes 0 and tiny delays like browsers do.
                durations[i] = (int)Math.Min(delay * 10, int.MaxValue);
            }
            finally
            {
                ComInterop.Release(ref reader);
                ComInterop.Release(ref frame);
            }
        }

        // A zero logical screen is malformed; browsers then size the canvas to the frames.
        int canvasWidth = (int)screenWidth, canvasHeight = (int)screenHeight;
        if (canvasWidth == 0 || canvasHeight == 0)
        {
            foreach (var f in frames)
            {
                canvasWidth = Math.Max(canvasWidth, f.Left + f.Width);
                canvasHeight = Math.Max(canvasHeight, f.Top + f.Height);
            }
        }

        var timeline = new AnimTimeline
        {
            Kind = AnimKind.Gif,
            Width = Math.Max(1, canvasWidth),
            Height = Math.Max(1, canvasHeight),
            LoopCount = loopCount,
            DurationsMs = durations,
            FrameHasAlpha = new bool[frameCount],
            GifFrames = frames,
        };
        timeline.TraceGifOpacity();
        return timeline;
    }


    /// <summary>
    /// Marks each GIF frame that may leave transparent pixels on the canvas, erring towards alpha.
    /// </summary>
    private void TraceGifOpacity()
    {
        var opaque = false;
        var opaqueBeforePrevious = false;

        for (var i = 0; i < GifFrames.Length; i++)
        {
            if (i > 0)
            {
                var disposal = GifFrames[i - 1].Disposal;
                if (disposal == DISPOSE_BACKGROUND) opaque = false;
                else if (disposal == DISPOSE_PREVIOUS) opaque = opaqueBeforePrevious;
            }

            opaqueBeforePrevious = opaque;

            // Transparent pixels leave the canvas as it was, so only a full opaque paint changes the verdict.
            if (!GifFrames[i].Transparent && CoversCanvas(GifFrames[i])) opaque = true;
            FrameHasAlpha[i] = !opaque;
        }
    }


    /// <summary>
    /// Maps the NETSCAPE2.0 repeat count onto play-throughs: no block plays once, 0 is forever, N repeats N times after the first.
    /// </summary>
    private static int ParseGifLoopCount(byte[]? application, byte[]? data)
    {
        if (application is null || data is null) return 1;

        var name = System.Text.Encoding.ASCII.GetString(application);
        if (name is not ("NETSCAPE2.0" or "ANIMEXTS1.0")) return 1;

        // WIC keeps the sub-block length byte in front: [3, 1, lo, hi].
        var offset = data.Length >= 4 && data[0] == 3 ? 1 : 0;
        if (data.Length < offset + 3 || data[offset] != 1) return 1;

        var repeats = data[offset + 1] | (data[offset + 2] << 8);
        return repeats == 0 ? 0 : repeats + 1;
    }


    private static AnimTimeline ReadWebp(IWICBitmapDecoder* decoder, int frameCount, string path)
    {
        var durations = new int[frameCount];
        uint loopCount = 0, width = 0, height = 0;

        IWICMetadataQueryReader* global = null;
        try
        {
            if (decoder->GetMetadataQueryReader(&global).Success)
            {
                WicMetadata.TryGetUInt(global, "/ANIM/LoopCount", out loopCount);
            }
        }
        finally
        {
            ComInterop.Release(ref global);
        }

        for (var i = 0; i < frameCount; i++)
        {
            IWICBitmapFrameDecode* frame = null;
            IWICMetadataQueryReader* reader = null;
            try
            {
                if (decoder->GetFrame((uint)i, &frame).Failure) continue;
                if (i == 0) frame->GetSize(&width, &height);

                uint duration = 0;
                if (frame->GetMetadataQueryReader(&reader).Success)
                {
                    WicMetadata.TryGetUInt(reader, "/ANMF/FrameDuration", out duration);
                }
                durations[i] = (int)Math.Min(duration, int.MaxValue);
            }
            finally
            {
                ComInterop.Release(ref reader);
                ComInterop.Release(ref frame);
            }
        }

        var hasAlpha = WebpHasAlpha(path);
        var frameAlpha = new bool[frameCount];
        Array.Fill(frameAlpha, hasAlpha);

        return new AnimTimeline
        {
            Kind = AnimKind.Webp,
            Width = (int)Math.Max(1, width),
            Height = (int)Math.Max(1, height),

            // WebP's loop count already counts play-throughs.
            LoopCount = (int)Math.Min(loopCount, int.MaxValue),
            DurationsMs = durations,
            FrameHasAlpha = frameAlpha,
        };
    }


    /// <summary>
    /// Reads the alpha bit from the WebP header; WIC decodes every WebP to BGRA, so the pixel format cannot tell.
    /// </summary>
    public static bool WebpHasAlpha(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[25];
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return true;

            var chunk = header[12..16];
            if (chunk.SequenceEqual("VP8X"u8)) return (header[20] & 0x10) != 0;
            if (chunk.SequenceEqual("VP8 "u8)) return false;

            // VP8L: after the 0x2F signature come 14-bit width-1, 14-bit height-1, then alpha_is_used.
            if (chunk.SequenceEqual("VP8L"u8)) return ((BitConverter.ToUInt32(header[21..25]) >> 28) & 1) != 0;
            return true;
        }
        catch
        {
            return true;
        }
    }
}


/// <summary>
/// Timelines of recently opened files, keyed by path and checked against the file's size and write time.
/// </summary>
internal static class AnimTimelineCache
{
    private const int MAX_ENTRIES = 8;

    private sealed record Entry(string Path, long Stamp, long Size, AnimTimeline Timeline);

    private static readonly Lock _lock = new();

    // Most recently used last.
    private static readonly List<Entry> _entries = [];


    public static AnimTimeline? Get(string path, long stamp, long size)
    {
        lock (_lock)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (!string.Equals(entry.Path, path, StringComparison.Ordinal)) continue;

                _entries.RemoveAt(i);
                if (entry.Stamp != stamp || entry.Size != size) return null;

                _entries.Add(entry);
                return entry.Timeline;
            }
            return null;
        }
    }


    public static void Put(string path, long stamp, long size, AnimTimeline timeline)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.Ordinal));
            _entries.Add(new Entry(path, stamp, size, timeline));
            if (_entries.Count > MAX_ENTRIES) _entries.RemoveAt(0);
        }
    }


    /// <summary>Reads the write time and size that identify one version of a file.</summary>
    public static bool TryStamp(string path, out long stamp, out long size)
    {
        stamp = size = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;

            stamp = info.LastWriteTimeUtc.Ticks;
            size = info.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
