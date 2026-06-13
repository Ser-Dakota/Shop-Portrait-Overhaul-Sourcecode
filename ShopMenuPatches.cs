using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ShopPortraitOverhaul
{
    /// <summary>
    /// Reflection cache for the private ShopMenu fields we need to reposition or suppress.
    /// </summary>
    internal static class ShopMenuFields
    {
        public static readonly FieldInfo PortraitTexture =
            AccessTools.Field(typeof(ShopMenu), "portraitTexture");
        public static readonly FieldInfo Inventory =
            AccessTools.Field(typeof(ShopMenu), "inventory");
        public static readonly FieldInfo ScrollBarRunner =
            AccessTools.Field(typeof(ShopMenu), "scrollBarRunner");
        public static readonly FieldInfo ForSaleButtons =
            AccessTools.Field(typeof(ShopMenu), "forSaleButtons");
        public static readonly FieldInfo UpArrow =
            AccessTools.Field(typeof(ShopMenu), "upArrow");
        public static readonly FieldInfo DownArrow =
            AccessTools.Field(typeof(ShopMenu), "downArrow");
        public static readonly FieldInfo ScrollBar =
            AccessTools.Field(typeof(ShopMenu), "scrollBar");
        public static readonly FieldInfo PortraitBox =
            AccessTools.Field(typeof(ShopMenu), "portraitBox");
        // "potrait" (missing 'r') is the actual spelling in the game source.
        public static readonly FieldInfo PotraitPersonDialogue =
            AccessTools.Field(typeof(ShopMenu), "potraitPersonDialogue");
        // upperRightCloseButton lives on IClickableMenu.
        public static readonly FieldInfo UpperRightCloseButton =
            AccessTools.Field(typeof(IClickableMenu), "upperRightCloseButton");
    }

    /// <summary>
    /// Persistent right-shift logic for ShopMenu when a custom portrait is active.
    ///
    /// The shift is applied once per layout pass and left in place between frames, so
    /// drawing positions and hit-test positions are always the same space. That makes
    /// mouse input, hover, controller snap, neighbor search, and scroll calculations
    /// all read coordinates that match what's drawn — no per-entry-point translation.
    ///
    /// A per-instance flag stored in a ConditionalWeakTable prevents double-shifting
    /// when a single layout pass triggers multiple hooks. The table holds weak references
    /// so closed shop menus are collected normally; there is no manual cleanup.
    ///
    /// Each layout method that resets coordinates back to vanilla (updatePosition,
    /// gameWindowSizeChanged, populateClickableComponentList) clears the flag in its
    /// Postfix and re-applies the shift on the freshly-laid-out menu.
    /// </summary>
    internal static class ShopMenuShifter
    {
        public const int Shift = 200;

        // Per-instance flag: "this ShopMenu's components are currently shifted by RequiredShift."
        // ConditionalWeakTable lets the GC collect closed menus without manual Remove() bookkeeping.
        private static readonly ConditionalWeakTable<ShopMenu, object> Shifted = new();

        public static bool ShouldShift =>
            ModEntry.ActivePortrait != null && Game1.options.showMerchantPortraits;

        /// <summary>Shifts the given menu by <see cref="ModEntry.RequiredShift"/> if it hasn't
        /// already been shifted this layout pass. Safe to call multiple times — only the first
        /// call within a layout pass takes effect.</summary>
        public static void ApplyShift(ShopMenu menu)
        {
            if (menu == null) return;
            if (!ShouldShift) return;
            int shift = ModEntry.RequiredShift;
            if (shift <= 0) return;
            if (Shifted.TryGetValue(menu, out _)) return;

            Shifted.Add(menu, new object());
            ShiftBy(menu, shift);
        }

        /// <summary>Clears the "already shifted" flag so the next ApplyShift call will take effect.
        /// Used by layout-method Postfixes, because those methods reset bounds back to vanilla and
        /// the next shift needs to operate on the fresh layout.</summary>
        public static void ResetShiftedFlag(ShopMenu menu)
        {
            if (menu == null) return;
            Shifted.Remove(menu);
        }

        private static void ShiftBy(ShopMenu menu, int offset)
        {
            menu.xPositionOnScreen += offset;

            // Collect every unique component reference reachable from the menu, deduped by
            // identity. Several of the lists below share references (e.g. allClickableComponents
            // typically contains the same objects as forSaleButtons and the inventory slot list
            // after populateClickableComponentList runs). Shifting each list independently would
            // move shared components more than once; the set guarantees one shift per object.
            var seen = new HashSet<ClickableComponent>(ReferenceEqualityComparer.Instance);

            CollectComponents(menu.allClickableComponents, seen);
            CollectEnumerable(ShopMenuFields.ForSaleButtons?.GetValue(menu), seen);
            CollectComponent(ShopMenuFields.UpArrow?.GetValue(menu) as ClickableComponent, seen);
            CollectComponent(ShopMenuFields.DownArrow?.GetValue(menu) as ClickableComponent, seen);
            CollectComponent(ShopMenuFields.ScrollBar?.GetValue(menu) as ClickableComponent, seen);
            CollectComponent(ShopMenuFields.PortraitBox?.GetValue(menu) as ClickableComponent, seen);
            CollectComponent(ShopMenuFields.UpperRightCloseButton?.GetValue(menu) as ClickableComponent, seen);

            if (ShopMenuFields.Inventory?.GetValue(menu) is InventoryMenu inv)
            {
                inv.xPositionOnScreen += offset;
                CollectComponents(inv.inventory, seen);
                CollectComponents(inv.allClickableComponents, seen);
            }

            foreach (ClickableComponent c in seen)
                c.bounds = new Rectangle(c.bounds.X + offset, c.bounds.Y, c.bounds.Width, c.bounds.Height);

            if (ShopMenuFields.ScrollBarRunner?.GetValue(menu) is Rectangle runner)
            {
                ShopMenuFields.ScrollBarRunner.SetValue(menu,
                    new Rectangle(runner.X + offset, runner.Y, runner.Width, runner.Height));
            }
        }

        private static void CollectComponents(List<ClickableComponent> components, HashSet<ClickableComponent> seen)
        {
            if (components == null) return;
            foreach (ClickableComponent c in components)
                if (c != null) seen.Add(c);
        }

        private static void CollectComponent(ClickableComponent component, HashSet<ClickableComponent> seen)
        {
            if (component != null) seen.Add(component);
        }

        private static void CollectEnumerable(object list, HashSet<ClickableComponent> seen)
        {
            if (list is not IEnumerable enumerable) return;
            foreach (object item in enumerable)
                if (item is ClickableComponent comp) seen.Add(comp);
        }
    }

    /// <summary>
    /// updatePosition rebuilds the menu's layout from xPositionOnScreen, which wipes any
    /// shift we previously applied. After it finishes, clear the flag and re-apply.
    /// </summary>
    [HarmonyPatch(typeof(ShopMenu), nameof(ShopMenu.updatePosition))]
    internal static class ShopMenu_UpdatePosition_Patch
    {
        static void Postfix(ShopMenu __instance)
        {
            ShopMenuShifter.ResetShiftedFlag(__instance);
            ShopMenuShifter.ApplyShift(__instance);
        }
    }

    /// <summary>
    /// Window resize rebuilds layout. Same reset + re-apply pattern as updatePosition.
    /// </summary>
    [HarmonyPatch(typeof(ShopMenu), nameof(ShopMenu.gameWindowSizeChanged))]
    internal static class ShopMenu_GameWindowSizeChanged_Patch
    {
        static void Postfix(ShopMenu __instance)
        {
            ShopMenuShifter.ResetShiftedFlag(__instance);
            ShopMenuShifter.ApplyShift(__instance);
        }
    }

    /// <summary>
    /// populateClickableComponentList rebuilds the master component list from existing
    /// per-field component references — it does not reposition them or reset bounds to
    /// vanilla. Calling <see cref="ShopMenuShifter.ResetShiftedFlag"/> here would be wrong:
    /// the components remain in their already-shifted positions, so a follow-up
    /// <see cref="ShopMenuShifter.ApplyShift"/> would double-shift them.
    ///
    /// This patch is therefore a guard / safety net rather than an active re-shift point.
    /// In normal operation the shifted-flag is set when this fires, so ApplyShift is a
    /// no-op. It only does real work in the narrow case where the flag was cleared but
    /// the menu has not yet been re-shifted (e.g. construction-time, where ApplyShift
    /// short-circuits anyway via ShouldShift because ActivePortrait is still null).
    ///
    /// Historical note: this hook was the source of an earlier controller-only
    /// double-shift bug — the rebuild merges references already present in per-field
    /// lists into allClickableComponents, and a naïve shift would move shared references
    /// twice. The identity-dedupe set inside ShiftBy is what actually prevents that;
    /// this patch is kept so the safety net stays in place.
    /// </summary>
    [HarmonyPatch(typeof(IClickableMenu), nameof(IClickableMenu.populateClickableComponentList))]
    internal static class ShopMenu_PopulateClickableComponentList_Patch
    {
        static void Postfix(IClickableMenu __instance)
        {
            if (__instance is not ShopMenu shop) return;
            ShopMenuShifter.ApplyShift(shop);
        }
    }

    /// <summary>
    /// Suppresses the vanilla portrait area (texture + dialogue) and the portrait box
    /// component's draw call. Position shifting is handled by ShopMenuShifter; this
    /// patch only hides the original portrait artwork during the draw.
    /// </summary>
    [HarmonyPatch(typeof(ShopMenu), "draw", typeof(SpriteBatch))]
    internal static class ShopMenu_Draw_Patch
    {
        // Kept as a public constant for callers that want a reference value. Live shift
        // amount is read from ModEntry.RequiredShift via ShopMenuShifter.
        internal const int Shift = ShopMenuShifter.Shift;
        internal static int EffectiveShift => ModEntry.RequiredShift;

        private struct DrawState
        {
            public bool Active;
            public Texture2D OrigTex;
            public string OrigDialogue;
        }

        static void Prefix(ShopMenu __instance, out DrawState __state)
        {
            __state = default;
            if (!ShopMenuShifter.ShouldShift)
                return;

            __state.Active = true;

            ModEntry.SuppressPortraitBox =
                ShopMenuFields.PortraitBox?.GetValue(__instance) as ClickableComponent;

            if (ShopMenuFields.PortraitTexture != null)
            {
                __state.OrigTex = ShopMenuFields.PortraitTexture.GetValue(__instance) as Texture2D;
                ShopMenuFields.PortraitTexture.SetValue(__instance, null);
            }
            if (ShopMenuFields.PotraitPersonDialogue != null)
            {
                __state.OrigDialogue = ShopMenuFields.PotraitPersonDialogue.GetValue(__instance) as string;
                ShopMenuFields.PotraitPersonDialogue.SetValue(__instance, null);
            }
        }

        static void Postfix(ShopMenu __instance, DrawState __state)
        {
            if (!__state.Active)
                return;

            ModEntry.SuppressPortraitBox = null;
            ShopMenuFields.PortraitTexture?.SetValue(__instance, __state.OrigTex);
            ShopMenuFields.PotraitPersonDialogue?.SetValue(__instance, __state.OrigDialogue);
        }
    }

    /// <summary>
    /// Skips the draw call for the portrait box component when suppression is active,
    /// leaving the component itself fully intact in all lists.
    /// </summary>
    [HarmonyPatch(typeof(ClickableTextureComponent), "draw", typeof(SpriteBatch))]
    internal static class PortraitBox_Draw_Patch
    {
        static bool Prefix(ClickableTextureComponent __instance)
        {
            if (ModEntry.SuppressPortraitBox != null && __instance == ModEntry.SuppressPortraitBox)
                return false;
            return true;
        }
    }
}
