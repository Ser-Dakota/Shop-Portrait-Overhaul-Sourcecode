using System.Collections.Generic;

namespace ShopPortraitOverhaul
{
    public class ShopPortraitToken
    {
        public bool IsMutable() => false;
        public bool AllowsInput() => false;
        public bool RequiresInput() => false;
        public bool CanHaveMultipleValues(string input = null) => false;
        public bool UpdateContext() => false;
        public bool IsReady() => true;

        public IEnumerable<string> GetValues(string input)
        {
            return new[] { "Custom/ShopPortraitOverhaul/Packs" };
        }
    }
}
