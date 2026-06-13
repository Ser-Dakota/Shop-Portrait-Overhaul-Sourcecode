using System.Collections.Generic;

namespace ShopPortraitOverhaul
{
    public class ModConfig
    {
        // Key = ShopId, Value = Pack UniqueID, "Auto", "Vanilla", or "Disabled"
        public Dictionary<string, string> ShopPreferences { get; set; } = new();

        // Global portrait display settings
        public int HorizontalOffset   { get; set; } = 0;
        public int VerticalOffset     { get; set; } = 0;
        public int PortraitScalePct   { get; set; } = 100;
        public float MaxHeightFraction { get; set; } = 0.90f;

        // When true, shifts the shop menu an extra 54px left to keep the scrollbar clear.
        public bool ShowScrollbar { get; set; } = false;
    }
}
