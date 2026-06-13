#nullable enable
using System.Collections.Generic;

namespace ShopPortraitOverhaul
{
    public class ShopPortraitSettings
    {
        public int?    DefaultScale   { get; set; }  // 50/75/100/125/150
        public int?    DefaultOffsetX { get; set; }
        public int?    DefaultOffsetY { get; set; }
        public int?    AndroidOffsetX { get; set; }  // optional Android-only override for DefaultOffsetX
        public int?    AndroidOffsetY { get; set; }  // optional Android-only override for DefaultOffsetY
        public string? AnchorMode     { get; set; }  // "BottomLeft" or "CenterLeft"

        // Optional sprite-sheet animation. Null (default) = static portrait; existing packs unaffected.
        public AnimationSettings? Animation { get; set; }
    }

    /// <summary>Optional sprite-sheet animation for a portrait. Packs that omit
    /// <see cref="ShopPortraitSettings.Animation"/> render a single static image.</summary>
    public class AnimationSettings
    {
        public int FrameWidth  { get; set; }
        public int FrameHeight { get; set; }
        public int FrameCount  { get; set; }
        public int Columns     { get; set; } = 0;    // 0 = single row (treated as Columns == FrameCount)
        public int?   FrameDurationMs { get; set; }  // uniform per-frame duration, milliseconds
        public int[]? FrameDurations  { get; set; }  // optional per-frame durations; overrides FrameDurationMs
    }

    public class PackRegistration
    {
        public string? PackName { get; set; }
        public string? Author   { get; set; }
        public Dictionary<string, ShopPortraitSettings?> Portraits { get; set; } = new();
    }
}
