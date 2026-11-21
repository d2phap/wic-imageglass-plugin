# WIC Codec 1.3.0 vs ImageGlass built-in codecs

How fast ImageGlass reads each format with the WIC Codec plugin, compared with its built-in codecs
(SkiaSharp for JPEG, PNG, BMP, GIF and WebP; Magick.NET for everything else).

Each file goes through ImageGlass's own read paths, the same calls the app makes:

- **Metadata**: `CodecRegistry.LoadMetadataAsync`, run when a photo is opened.
- **Full decode**: `CodecRegistry.DecodeAsync` at full size, what the viewer shows.
- **Thumbnail**: `PhotoPreviewProvider.GetThumbnailAsync` with the gallery's default request (a 210 px cell, 420 px box).

Times are medians in milliseconds, **built-in → WIC Codec**. Lower is better.

🟢 WIC is faster · 🟡 WIC is slower · 🟠 WIC is 2× slower or more · unmarked: within 5%


## Results

| Format | Sample | Metadata | Full decode | Thumbnail |
|---|---|---|---|---|
| JPEG | 26 MP | 4.8 → 🟢 0.8 | 160 → 🟢 99 | 78 → 🟢 73 |
| JPEG | 16 MP, EXIF-rotated | 4.0 → 🟢 0.8 | 230 → 🟢 64 | 31 → 🟡 34 |
| JPEG | 144 MP | 6.5 → 🟢 0.6 | 506 → 🟢 275 | 158 → 🟢 149 |
| PNG | 944 px, 16-bit HDR ¹ | 3.8 → 🟢 1.3 | 17 → 🟡 26 | 20 → 🟡 28 |
| PNG | 534 px, alpha | 2.3 → 🟢 0.5 | 0.6 → 🟡 1.0 | 1.9 → 🟡 2.1 |
| BMP | 992 × 980 | 5.8 → 🟢 0.5 | 2.2 → 🟡 3.1 | 5.9 → 🟢 4.3 |
| GIF | 800 × 600, 176 frames ² | 16.3 → 🟢 0.5 | 0.4 → 🟢 0.2 | 1.2 → 🟠 5.2 |
| GIF | 410 × 329, 2 frames ² | 2.3 → 🟢 0.4 | 0.2 → 0.2 | 0.2 → 🟠 1.4 |
| WebP | 22 MP | 5.3 → 🟢 0.8 | 111 → 🟠 247 | 86 → 🟠 226 |
| WebP | 39 MP | 18.1 → 🟢 0.8 | 346 → 🟡 507 | 334 → 🟡 483 |
| WebP | 1024 × 429, 62 frames ² | 13.3 → 🟢 3.2 | 13.5 → 🟢 0.4 | 7.9 → 🟡 9.8 |
| TIFF | 512 × 512 | 4.3 → 🟢 0.5 | 5.4 → 🟢 1.0 | 9.4 → 🟢 2.1 |
| TIFF | 1419 × 1001, LZW | 6.1 → 🟢 0.6 | fails → 🟢 17.5 | fails → 🟢 18.4 |
| HEIC | 12 MP | 27.1 → 🟢 2.1 | 295 → 🟢 241 | 300 → 🟢 120 |
| HEIF | 12 MP | 10.8 → 🟢 1.8 | 276 → 278 | 283 → 🟢 152 |
| AVIF | 1 MP, 8-bit | 4.2 → 🟢 0.9 | 74 → 🟢 19 | 78 → 🟢 19 |
| AVIF | 5 MP, HDR | 88 → 🟢 38 ³ | 496 → 🟢 156 | 516 → 🟢 104 |
| JPEG XL | 24 MP, SDR ⁴ | 5.5 → 🟢 2.9 | 472 → 🟠 984 | 479 → 🟠 1012 |
| JPEG XR | 2560 × 1440, HDR | cannot open → 🟢 0.6 | cannot open → 🟢 271 | cannot open → 🟢 46 |
| JPEG XR | 2560 × 1440 | cannot open → 🟢 0.6 | cannot open → 🟢 117 | cannot open → 🟢 27 |
| WDP | 1200 × 800 | cannot open → 🟢 0.5 | cannot open → 🟢 40 | cannot open → 🟢 37 |
| DDS | 2048 × 2048 | 3.6 → 🟢 0.6 | 1515 → 🟢 8.7 | 1533 → 🟢 13 |
| ICO | 13 sizes, up to 256 px ⁵ | 3.3 → 🟢 0.4 | 1.6 → 🟢 0.4 | 0.9 → 🟡 1.3 |
| Sony ARW | 24 MP | 12.4 → 🟢 2.6 | 880 → 🟢 519 | 7.1 → 🟡 13.9 |
| Canon CR2 | 8 MP | 4.7 → 🟢 1.7 | 422 → 🟢 275 | 3.5 → 🟠 11.0 |
| Nikon NEF | 36 MP | 14.3 → 🟢 3.3 | 1640 → 🟢 1088 | 24.8 → 🟡 38.5 |
| Leica DNG | 18 MP ⁶ | 7.3 → 🟢 2.9 | 730 → 🟢 444 | 51 → 🟠 433 |

**Summary**

- Metadata is 2–32× faster for every format.
- Faster full decodes: JPEG 1.6–3.6×, AVIF 3–4×, TIFF ~5×, camera RAW 1.5–1.7×, DDS over 100×.
- Faster thumbnails: AVIF 4–5×, HEIC and HEIF ~2×.
- WIC opens JPEG XR, WDP and some LZW TIFFs that the built-in codecs cannot.
- Slower: WebP stills 1.5–2.6×, JPEG XL ~2×, camera RAW thumbnails 1.5–3× (8.5× for the DNG, see note 6). Small PNG, BMP and GIF files are slower too, but they take only a few milliseconds either way.

**Notes**

1. WIC keeps the full 16 bits per channel; the built-in decoder reduces it to 8 bits.
2. For an animated file, "full decode" only builds the animator, and frames are decoded during playback. Decoding one frame of an animated WebP takes 1.5–4.4 ms on average (9.1 ms at most) against frame delays of 41–80 ms, in-order and warm.
3. Bimodal: 1.6 ms when the plugin had read the previous file too, 35–60 ms right after a Magick.NET decode.
4. Microsoft's JPEG XL decoder outputs 16-bit floating-point pixels even for an 8-bit image, which costs time and doubles the memory.
5. Not the same work: the built-in codec decodes the icon's first entry (48 × 48), WIC its first frame (256 × 256).
6. This camera embeds only a 320 px preview, smaller than the gallery's 420 px request, so the thumbnail falls back to a full decode. Cameras that embed a 1024 px or larger preview get the fast path, as the ARW, CR2 and NEF rows show.
