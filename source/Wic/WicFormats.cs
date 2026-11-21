/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using Vortice.Win32;
using Vortice.Win32.Com;
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

/// <summary>
/// What one installed WIC encoder can do with a given extension.
/// </summary>
internal readonly record struct EncoderTarget(Guid ContainerFormat, bool SupportsMultiFrame,
    bool SupportsAnimation, bool IsBuiltIn);


/// <summary>
/// The set of formats this machine's WIC actually has codecs for, discovered once at plugin
/// load by enumerating the registered components.
/// <para>
/// Enumerating rather than hardcoding is the whole point: a machine with the Microsoft Store
/// HEIF / AV1 / RAW / WebP extensions installed exposes those formats here with no plugin
/// change, and so does any third-party WIC codec.
/// </para>
/// </summary>
internal static unsafe class WicFormats
{
    /// <summary>
    /// Used when component enumeration fails outright, so JPEG XR still works on a machine
    /// whose component registry we could not read.
    /// </summary>
    private static readonly string[] _fallbackDecodeExtensions =
        [".bmp", ".dib", ".gif", ".ico", ".cur", ".jpg", ".jpeg", ".jpe", ".jfif", ".exif",
         ".png", ".tif", ".tiff", ".jxr", ".hdp", ".wdp", ".dds"];

    private static readonly (string Ext, Guid Container)[] _fallbackEncodeTargets =
    [
        (".bmp", WIC.GUID_ContainerFormatBmp),
        (".dib", WIC.GUID_ContainerFormatBmp),
        (".gif", WIC.GUID_ContainerFormatGif),
        (".jpg", WIC.GUID_ContainerFormatJpeg),
        (".jpeg", WIC.GUID_ContainerFormatJpeg),
        (".png", WIC.GUID_ContainerFormatPng),
        (".tif", WIC.GUID_ContainerFormatTiff),
        (".tiff", WIC.GUID_ContainerFormatTiff),
        (".jxr", WIC.GUID_ContainerFormatWmp),
        (".hdp", WIC.GUID_ContainerFormatWmp),
        (".wdp", WIC.GUID_ContainerFormatWmp),
    ];

    /// <summary>
    /// Extensions a container handles but does not advertise. Windows' own JPEG XR codec
    /// enumerates only ".wdp" and ".jxr", yet reads and writes ".hdp" (the original HD Photo
    /// name) perfectly well — it is the same container, and files in the wild still use it.
    /// </summary>
    private static readonly (Guid Container, string[] Extensions)[] _containerAliases =
    [
        (WIC.GUID_ContainerFormatWmp, [".jxr", ".wdp", ".hdp"]),
    ];


    /// <summary>Extensions any installed WIC decoder claims (lowercase, leading dot).</summary>
    public static string[] DecodeExtensions { get; private set; } = [];

    /// <summary>Extensions any installed WIC encoder claims (lowercase, leading dot).</summary>
    public static string[] EncodeExtensions { get; private set; } = [];

    private static Dictionary<string, EncoderTarget> _encoders = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// Resolves the encoder that should write <paramref name="extension"/>.
    /// </summary>
    public static bool TryGetEncoder(string extension, out EncoderTarget target)
        => _encoders.TryGetValue(extension, out target);


    /// <summary>
    /// Enumerates the registered WIC decoders and encoders. Safe to call once; later calls
    /// overwrite the tables.
    /// </summary>
    public static void Discover()
    {
        var factory = WicFactory.Create();
        if (factory == null)
        {
            ApplyFallback();
            return;
        }

        try
        {
            var decode = new List<string>();
            var decodeSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var decodeContainers = new HashSet<Guid>();
            var encoders = new Dictionary<string, EncoderTarget>(StringComparer.OrdinalIgnoreCase);

            EnumerateDecoders(factory, decode, decodeSeen, decodeContainers);
            EnumerateEncoders(factory, encoders);
            ApplyContainerAliases(decode, decodeSeen, decodeContainers, encoders);

            if (decode.Count == 0 || encoders.Count == 0)
            {
                ApplyFallback();
                return;
            }

            DecodeExtensions = [.. decode];
            _encoders = encoders;
            EncodeExtensions = [.. encoders.Keys];

            HostChannel.Log(2, $"WicCodec: {DecodeExtensions.Length} readable, "
                + $"{EncodeExtensions.Length} writable extensions discovered.");
        }
        catch (Exception ex)
        {
            HostChannel.Log(3, $"WicCodec: component enumeration failed, using the built-in list. {ex.Message}");
            ApplyFallback();
        }
        finally
        {
            ComInterop.Release(ref factory);
        }
    }


    private static void ApplyFallback()
    {
        DecodeExtensions = _fallbackDecodeExtensions;

        var encoders = new Dictionary<string, EncoderTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ext, container) in _fallbackEncodeTargets)
        {
            var multi = container == WIC.GUID_ContainerFormatTiff || container == WIC.GUID_ContainerFormatGif;
            encoders[ext] = new EncoderTarget(container, multi, container == WIC.GUID_ContainerFormatGif, true);
        }

        _encoders = encoders;
        EncodeExtensions = [.. encoders.Keys];
    }


    /// <summary>
    /// Fills in the extensions a discovered container is known to handle but does not enumerate.
    /// Only containers that really were found are extended, so this never claims a format the
    /// machine has no codec for.
    /// </summary>
    private static void ApplyContainerAliases(List<string> decode, HashSet<string> decodeSeen,
        HashSet<Guid> decodeContainers, Dictionary<string, EncoderTarget> encoders)
    {
        foreach (var (container, extensions) in _containerAliases)
        {
            if (decodeContainers.Contains(container))
            {
                foreach (var ext in extensions)
                {
                    if (decodeSeen.Add(ext)) decode.Add(ext);
                }
            }

            // Reuse the traits of an extension the encoder already claimed for this container,
            // rather than assuming what it supports.
            EncoderTarget? template = null;
            foreach (var entry in encoders.Values)
            {
                if (entry.ContainerFormat != container) continue;
                template = entry;
                break;
            }
            if (template is null) continue;

            foreach (var ext in extensions)
            {
                if (!encoders.ContainsKey(ext)) encoders[ext] = template.Value;
            }
        }
    }


    private static void EnumerateDecoders(IWICImagingFactory* factory, List<string> into,
        HashSet<string> seen, HashSet<Guid> containers)
    {
        IEnumUnknown* enumerator = null;
        if (factory->CreateComponentEnumerator((uint)WIC.WICDecoder,
            (uint)WIC.WICComponentEnumerateDefault, &enumerator).Failure) return;

        try
        {
            var codecIid = IWICBitmapCodecInfo.IID_IWICBitmapCodecInfo;
            IUnknown* unknown = null;
            uint fetched = 0;

            while (enumerator->Next(1, &unknown, &fetched).Value == ComInterop.S_OK && fetched == 1)
            {
                IWICBitmapCodecInfo* info = null;
                try
                {
                    if (unknown->QueryInterface(&codecIid, (void**)&info).Failure) continue;

                    Guid container;
                    if (info->GetContainerFormat(&container).Success) containers.Add(container);

                    foreach (var ext in ReadExtensions(info))
                    {
                        if (seen.Add(ext)) into.Add(ext);
                    }
                }
                finally
                {
                    ComInterop.Release(ref info);
                    ComInterop.Release(ref unknown);
                }
            }
        }
        finally
        {
            ComInterop.Release(ref enumerator);
        }
    }


    private static void EnumerateEncoders(IWICImagingFactory* factory, Dictionary<string, EncoderTarget> into)
    {
        IEnumUnknown* enumerator = null;
        if (factory->CreateComponentEnumerator((uint)WIC.WICEncoder,
            (uint)WIC.WICComponentEnumerateDefault, &enumerator).Failure) return;

        try
        {
            var codecIid = IWICBitmapCodecInfo.IID_IWICBitmapCodecInfo;
            var builtInVendor = WIC.GUID_VendorMicrosoftBuiltIn;
            var msVendor = WIC.GUID_VendorMicrosoft;
            IUnknown* unknown = null;
            uint fetched = 0;

            while (enumerator->Next(1, &unknown, &fetched).Value == ComInterop.S_OK && fetched == 1)
            {
                IWICBitmapCodecInfo* info = null;
                try
                {
                    if (unknown->QueryInterface(&codecIid, (void**)&info).Failure) continue;

                    Guid container;
                    if (info->GetContainerFormat(&container).Failure) continue;

                    Guid vendor;
                    var isBuiltIn = info->GetVendorGUID(&vendor).Success
                        && (vendor == builtInVendor || vendor == msVendor);

                    Bool32 multiFrame = default;
                    Bool32 animation = default;
                    info->DoesSupportMultiframe(&multiFrame);
                    info->DoesSupportAnimation(&animation);

                    var target = new EncoderTarget(container, multiFrame.Value != 0,
                        animation.Value != 0, isBuiltIn);

                    foreach (var ext in ReadExtensions(info))
                    {
                        // First writer wins, except that a Microsoft codec displaces a third-party
                        // one: a machine can register several encoders for the same extension and
                        // enumeration order is not meaningful.
                        if (into.TryGetValue(ext, out var existing) && !(target.IsBuiltIn && !existing.IsBuiltIn))
                        {
                            continue;
                        }
                        into[ext] = target;
                    }
                }
                finally
                {
                    ComInterop.Release(ref info);
                    ComInterop.Release(ref unknown);
                }
            }
        }
        finally
        {
            ComInterop.Release(ref enumerator);
        }
    }


    /// <summary>
    /// Reads and normalizes one codec's comma-separated extension list (e.g. <c>".jxr,.wdp,.hdp"</c>).
    /// </summary>
    private static List<string> ReadExtensions(IWICBitmapCodecInfo* info)
    {
        var result = new List<string>();

        uint needed = 0;
        if (info->GetFileExtensions(0, null, &needed).Failure || needed == 0) return result;

        var buffer = new char[needed];
        fixed (char* pBuffer = buffer)
        {
            if (info->GetFileExtensions(needed, pBuffer, &needed).Failure) return result;
        }

        var raw = new string(buffer, 0, (int)Math.Max(0, Math.Min(needed, buffer.Length)));
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = part.ToLowerInvariant().TrimEnd('\0');
            if (ext.Length < 2) continue;
            if (!ext.StartsWith('.')) ext = "." + ext;
            result.Add(ext);
        }

        return result;
    }
}
