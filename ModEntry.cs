using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ShopPortraitOverhaul
{
    public class ModEntry : Mod
    {
        // Set when a ShopMenu with a matching custom portrait opens; cleared on close.
        internal static Texture2D ActivePortrait;
        // Per-shop pack settings for the active portrait (offsets/scale); may be null
        // when the chosen pack has no per-shop overrides — draw falls back to config.
        internal static ShopPortraitSettings ActiveSettings;
        // Set to the portraitBox instance during ShopMenu.draw so PortraitBox_Draw_Patch
        // can skip just that one component's draw call without nulling the field.
        internal static ClickableComponent SuppressPortraitBox;
        // How far the ShopMenu is shifted right to clear the portrait. Derived from the
        // portrait's actual size on shop-open and window-resize; read by the draw/input patches.
        internal static int RequiredShift;

        private ModConfig _config;
        private IGenericModConfigMenuApi _gmcm;
        private bool _gmcmRegistered;
        // Holds a pending "Set All" selection across the setValue → save callback boundary.
        private string _pendingSetAll;

        // Per-shop portrait animation state; reset whenever the active shop changes.
        private int _animFrame;
        private double _animElapsed;

        public override void Entry(IModHelper helper)
        {
            _config = helper.ReadConfig<ModConfig>();

            // The three layout re-shift patches (updatePosition, gameWindowSizeChanged,
            // populateClickableComponentList) target methods that either don't exist or aren't
            // needed on Android — ShopMenu.updatePosition in particular is missing on Android
            // and crashes Harmony at startup. Android never shifts the menu (EffectiveShift is
            // forced to 0 in ComputePortraitLayout), so only the two draw-suppression patches
            // are required there.
            var harmony = new Harmony(ModManifest.UniqueID);
            if (Constants.TargetPlatform == GamePlatform.Android)
            {
                harmony.CreateClassProcessor(typeof(ShopMenu_Draw_Patch)).Patch();
                harmony.CreateClassProcessor(typeof(PortraitBox_Draw_Patch)).Patch();
            }
            else
            {
                harmony.PatchAll();
            }

            // Register the packs dictionary asset — content packs EditData into this.
            helper.Events.Content.AssetRequested += (_, e) =>
            {
                if (e.NameWithoutLocale.IsEquivalentTo("Custom/ShopPortraitOverhaul/Packs"))
                    e.LoadFrom(() => new Dictionary<string, PackRegistration>(), AssetLoadPriority.Low);
            };

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.Content.AssetReady += OnAssetReady;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.Display.RenderedActiveMenu += OnRenderedActiveMenu;
            helper.Events.Display.WindowResized += OnWindowResized;

            helper.ConsoleCommands.Add(
                "spo_export",
                "Exports current SPO settings to SPO_AuthorExport.json for pack authors.",
                (cmd, args) => ExportSettingsForPackAuthors()
            );
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            // Register CP token so content packs can use {{Dakota.ShopPortraitOverhaul/Packs}}
            // as a Target in their EditData entries.
            var cpApi = Helper.ModRegistry.GetApi<IContentPatcherApi>("Pathoschild.ContentPatcher");
            cpApi?.RegisterToken(ModManifest, "Packs", new ShopPortraitToken());

            // Store GMCM reference — actual menu registration is deferred until packs are loaded.
            _gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");

            // Initial registration (likely Vanilla-only — CP EditData patches aren't applied yet).
            GetPacks(); // prime the asset pipeline
            RegisterGmcm();

            // CP doesn't consistently apply its EditData on the title screen at a predictable time.
            // Poll every second: invalidate + reload the Packs asset so CP gets a chance to apply
            // its patch. OnAssetReady fires the moment pack data becomes available and re-registers.
            Helper.Events.GameLoop.OneSecondUpdateTicked += PollTitleScreenPacks;
        }

        private void PollTitleScreenPacks(object sender, OneSecondUpdateTickedEventArgs e)
        {
            // Once in-game, OnSaveLoaded handles re-registration — no need to poll anymore.
            if (Context.IsWorldReady)
            {
                Helper.Events.GameLoop.OneSecondUpdateTicked -= PollTitleScreenPacks;
                return;
            }

            // Force-reload the Packs asset. When CP has its EditData ready, this load will
            // return count > 0, OnAssetReady fires, and GMCM gets re-registered with pack options.
            Helper.GameContent.InvalidateCache("Custom/ShopPortraitOverhaul/Packs");
            GetPacks();
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            var packs = GetPacks();

            // Heal config: reset any preference pointing at a pack that's no longer installed.
            bool dirty = false;
            foreach (var shopId in _config.ShopPreferences.Keys.ToList())
            {
                var saved = _config.ShopPreferences[shopId];
                if (saved == "Vanilla" || packs.ContainsKey(saved)) continue;
                _config.ShopPreferences[shopId] = GetDefaultPackId(shopId, packs);
                dirty = true;
            }
            if (dirty) Helper.WriteConfig(_config);

            // CP patches are guaranteed applied by SaveLoaded, so register GMCM now.
            RegisterGmcm();
        }

        private void OnAssetReady(object sender, AssetReadyEventArgs e)
        {
            if (!e.NameWithoutLocale.IsEquivalentTo("Custom/ShopPortraitOverhaul/Packs"))
                return;

            if (GetPacks().Count > 0)
            {
                // Stop polling — pack data is available.
                Helper.Events.GameLoop.OneSecondUpdateTicked -= PollTitleScreenPacks;
                RegisterGmcm();
            }
        }

        private void RegisterGmcm()
        {
            if (_gmcm == null) return;

            // Unregister first so we can rebuild with fresh allowedValues arrays.
            // allowedValues is string[] baked at registration — there is no Func<string[]> overload
            // in GMCM, so re-registration is the only way to pick up newly-loaded pack data.
            if (_gmcmRegistered)
                _gmcm.Unregister(ModManifest);
            _gmcmRegistered = true;

            var packs = GetPacks();

            var hardcodedShops = GetShops()
                .Concat(GetDesertFestivalShops())
                .Concat(GetMiscShops())
                .ToList();

            // Shop IDs referenced by installed packs that aren't among the hardcoded shops —
            // discovered dynamically so packs can add portraits for custom (modded) shops.
            var knownShopIds = new HashSet<string>(
                hardcodedShops.Select(s => s.ShopId), StringComparer.OrdinalIgnoreCase);
            var modAddedShopIds = packs.Values
                .SelectMany(p => p.Portraits.Keys)
                .Where(id => !string.IsNullOrEmpty(id) && !knownShopIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Every shop the "Set All" master dropdown applies to (vanilla + mod-added),
            // so mod-added shops behave exactly like the hardcoded ones.
            var allShops = hardcodedShops
                .Concat(modAddedShopIds.Select(id => (ShopId: id, DisplayName: id)))
                .ToList();

            // ── Master "Set All" dropdown ─────────────────────────────────────────
            // Appears at the top of the config page.
            // setValue only stores the selection in _pendingSetAll — it does NOT write to _config
            // directly, because GMCM calls setValue for every field in registration order before
            // calling save(). If we applied it here, the 40+ individual-shop setValues that follow
            // would overwrite it. The save() callback applies _pendingSetAll last, after all per-shop
            // setValues have run, so its changes always win.
            const string neutralOption = "— Select to apply all —";
            var allPackValues = BuildAllPackValues(neutralOption, packs);

            _gmcm.Register(
                mod: ModManifest,
                reset: () =>
                {
                    _config.HorizontalOffset  = 0;
                    _config.VerticalOffset    = 0;
                    _config.PortraitScalePct  = 100;
                    _config.MaxHeightFraction = 0.90f;
                    _config.ShowScrollbar     = false;
                    _config.ShopPreferences.Clear();
                    _pendingSetAll = null;
                },
                save: () =>
                {
                    // Apply "Set All" last — after all per-shop setValue calls have already run.
                    // Vanilla is valid for every shop, so it overwrites unconditionally; any other
                    // pack only overwrites shops it actually provides a portrait for, so shops not
                    // covered by the pack keep their existing preference.
                    if (_pendingSetAll != null)
                    {
                        bool isVanilla = _pendingSetAll == "Vanilla";
                        packs.TryGetValue(_pendingSetAll, out var selectedPack);
                        foreach (var (sId, _) in allShops)
                        {
                            if (isVanilla || (selectedPack != null && selectedPack.Portraits.ContainsKey(sId)))
                                _config.ShopPreferences[sId] = _pendingSetAll;
                        }
                        _pendingSetAll = null;
                    }
                    Helper.WriteConfig(_config);
                }
            );

            _gmcm.AddTextOption(
                mod: ModManifest,
                name: () => "Set All Portraits",
                tooltip: () => "Apply one portrait pack to every shop at once.",
                getValue: () => neutralOption,
                setValue: value => _pendingSetAll = (value == neutralOption) ? null : value,
                allowedValues: allPackValues,
                formatAllowedValue: value =>
                {
                    if (value == neutralOption) return neutralOption;
                    if (value is "Vanilla") return "Vanilla";
                    return packs.TryGetValue(value, out var p) ? p.PackName : value;
                }
            );

            // ── Per-shop helper ───────────────────────────────────────────────────
            // setValue only updates _config in memory; save() writes once at the end.
            void AddShopOption(string shopId, string displayName)
            {
                _gmcm.AddTextOption(
                    mod: ModManifest,
                    name: () => displayName,
                    tooltip: () => $"Portrait source for {displayName}",
                    getValue: () =>
                    {
                        var pref = _config.ShopPreferences.GetValueOrDefault(shopId, null);
                        return pref ?? GetDefaultPackId(shopId, packs);
                    },
                    setValue: value => _config.ShopPreferences[shopId] = value,
                    allowedValues: BuildAllowedValues(shopId, packs),
                    formatAllowedValue: value =>
                    {
                        if (value is "Vanilla") return "Vanilla";
                        return packs.TryGetValue(value, out var p) ? p.PackName : value;
                    }
                );
            }

            // ── Global display settings ───────────────────────────────────────────
            _gmcm.AddSectionTitle(ModManifest, () => "Portrait Display");

            _gmcm.AddNumberOption(
                mod: ModManifest,
                name: () => "Horizontal Offset",
                tooltip: () => "Shift the portrait left (negative) or right (positive) in pixels.",
                getValue: () => _config.HorizontalOffset,
                setValue: v => _config.HorizontalOffset = v,
                min: -200, max: 200, interval: 5
            );

            _gmcm.AddNumberOption(
                mod: ModManifest,
                name: () => "Vertical Offset",
                tooltip: () => "Shift the portrait up (negative) or down (positive) in pixels.",
                getValue: () => _config.VerticalOffset,
                setValue: v => _config.VerticalOffset = v,
                min: -200, max: 200, interval: 5
            );

            var scaleOptions = new[] { "50", "75", "100", "125", "150" };
            _gmcm.AddTextOption(
                mod: ModManifest,
                name: () => "Portrait Scale",
                tooltip: () => "Scale the portrait. Independent of game UI scaling.",
                getValue: () => _config.PortraitScalePct.ToString(),
                setValue: v => _config.PortraitScalePct = int.Parse(v),
                allowedValues: scaleOptions,
                formatAllowedValue: v => v switch
                {
                    "50"  => "Extra Small",
                    "75"  => "Small",
                    "100" => "Default",
                    "125" => "Large",
                    "150" => "Extra Large",
                    _     => v
                }
            );

            _gmcm.AddNumberOption(
                mod: ModManifest,
                name: () => "Maximum Portrait Height",
                tooltip: () => "Clamp the portrait to this fraction of the viewport height.",
                getValue: () => _config.MaxHeightFraction,
                setValue: v => _config.MaxHeightFraction = v,
                min: 0.1f, max: 1.0f, interval: 0.05f
            );

            _gmcm.AddBoolOption(
                mod: ModManifest,
                name: () => "Show Scrollbar",
                tooltip: () => "Shifts the shop menu slightly left to keep the scrollbar visible. "
                             + "May reduce portrait space at smaller window sizes.",
                getValue: () => _config.ShowScrollbar,
                setValue: v => _config.ShowScrollbar = v
            );

            // ── Shops ─────────────────────────────────────────────────────────────
            _gmcm.AddSectionTitle(ModManifest, () => "Shops");
            foreach (var (shopId, displayName) in GetShops())
                AddShopOption(shopId, displayName);

            // ── Desert Festival ───────────────────────────────────────────────────
            _gmcm.AddSectionTitle(ModManifest, () => "Desert Festival");
            foreach (var (shopId, displayName) in GetDesertFestivalShops())
                AddShopOption(shopId, displayName);

            // ── Misc Shops ────────────────────────────────────────────────────────
            _gmcm.AddSectionTitle(ModManifest, () => "Misc Shops");
            foreach (var (shopId, displayName) in GetMiscShops())
                AddShopOption(shopId, displayName);

            // ── Mod-Added Shops ───────────────────────────────────────────────────
            // Shop IDs discovered from installed packs that aren't vanilla shops; they
            // get the same dropdown, settings, and behavior as the hardcoded shops above.
            if (modAddedShopIds.Count > 0)
            {
                _gmcm.AddSectionTitle(ModManifest, () => "Mod-Added Shops");
                foreach (var shopId in modAddedShopIds)
                    AddShopOption(shopId, shopId);
            }
        }

        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            if (e.NewMenu is ShopMenu shop)
            {
                // Restart portrait animation from the first frame for the newly-opened shop.
                _animFrame = 0;
                _animElapsed = 0;

                if (!Game1.options.showMerchantPortraits)
                {
                    ActivePortrait = null;
                    ActiveSettings = null;
                    return;
                }

                string shopId = shop.ShopId;

                // Hospital special case — different NPC runs it depending on day.
                if (shopId == "Hospital")
                {
                    int day = Game1.dayOfMonth % 7; // 1=Mon, 2=Tue, 3=Wed, 4=Thu, 5=Fri, 6=Sat, 0=Sun
                    shopId = (day == 2 || day == 4) ? "Hospital_Maru" : "Hospital_Harvey";
                }

                if (string.IsNullOrEmpty(shopId))
                {
                    ActivePortrait = null;
                    ActiveSettings = null;
                    return;
                }

                ActivePortrait = GetPortraitForShop(shopId);
                ActiveSettings = GetSettingsForShop(shopId);

                // Recompute the menu shift from the portrait's current size. Must run before
                // the controller re-snap below and before the first draw, both of which read it.
                UpdateRequiredShift(shop);

                // Apply the persistent menu shift before the controller re-snap below — the
                // re-snap reads component bounds and must see them already in shifted space.
                ShopMenuShifter.ApplyShift(shop);

                // The ShopMenu constructor already snapped the controller cursor to a component
                // before ActivePortrait was known here. Re-snap now that ApplyShift (above) has
                // moved the component bounds into shifted space, so the cursor lands where the
                // menu is actually drawn. This is a no-op for mouse players (snappyMenus off)
                // and for Vanilla shops (ActivePortrait is null).
                if (ActivePortrait != null
                    && Game1.options.snappyMenus
                    && shop.currentlySnappedComponent != null)
                {
                    shop.snapCursorToCurrentSnappedComponent();
                }
            }
            else if (e.OldMenu is ShopMenu)
            {
                ActivePortrait = null;
                ActiveSettings = null;
                RequiredShift = 0;
                _animFrame = 0;
                _animElapsed = 0;
            }
        }

        private void OnRenderedActiveMenu(object sender, RenderedActiveMenuEventArgs e)
        {
            if (!Game1.options.showMerchantPortraits)
                return;
            if (ActivePortrait == null)
                return;
            if (Game1.activeClickableMenu is not ShopMenu shop)
                return;

            // Recomputed each frame so window-resize changes are reflected immediately.
            Rectangle dest = ComputePortraitLayout(shop).Rect;

            // Animated portraits draw one frame via a source rect; static portraits pass
            // null (whole texture), exactly as before — packs without Animation are unaffected.
            Rectangle? sourceRect = AdvanceAnimationFrame();

            e.SpriteBatch.Draw(
                ActivePortrait,
                dest,
                sourceRect,
                Color.White,
                0f,
                Vector2.Zero,
                SpriteEffects.None,
                1f
            );
        }

        /// <summary>Advances the portrait animation timer and returns the current frame's
        /// source rectangle, or null for a static portrait (or an Animation block with
        /// invalid dimensions, which is treated as static).</summary>
        private Rectangle? AdvanceAnimationFrame()
        {
            var anim = ActiveSettings?.Animation;
            if (anim == null || anim.FrameCount <= 0 || anim.FrameWidth <= 0 || anim.FrameHeight <= 0)
                return null;

            _animElapsed += Game1.currentGameTime?.ElapsedGameTime.TotalMilliseconds ?? 0.0;

            int frameDuration = ResolveFrameDuration(anim, _animFrame);
            if (frameDuration > 0 && _animElapsed >= frameDuration)
            {
                _animFrame = (_animFrame + 1) % anim.FrameCount;
                _animElapsed = 0;
            }

            int cols = anim.Columns > 0 ? anim.Columns : anim.FrameCount;
            int row = _animFrame / cols;
            int col = _animFrame % cols;
            return new Rectangle(
                col * anim.FrameWidth, row * anim.FrameHeight,
                anim.FrameWidth, anim.FrameHeight);
        }

        /// <summary>Resolves the current frame's duration in milliseconds: the matching
        /// FrameDurations entry when supplied, otherwise the uniform FrameDurationMs
        /// (defaulting to 100ms when neither is set).</summary>
        private static int ResolveFrameDuration(AnimationSettings anim, int frame)
        {
            int[] durations = anim.FrameDurations;
            if (durations != null && frame >= 0 && frame < durations.Length)
                return durations[frame];
            return anim.FrameDurationMs ?? 100;
        }

        private void OnWindowResized(object sender, WindowResizedEventArgs e)
        {
            // The portrait size and the menu origin both depend on the viewport, so the
            // shift must be recomputed on resize. Only relevant while a shop is open.
            if (Game1.activeClickableMenu is ShopMenu shop)
            {
                UpdateRequiredShift(shop);
                ShopMenuShifter.ApplyShift(shop);
            }
        }

        // Breathing room kept between the portrait's right edge and the shop menu's frame.
        private const int PortraitMenuGap = 24;

        /// <summary>Recomputes <see cref="RequiredShift"/> from the portrait's current layout.
        /// Called on shop-open and window-resize — the only times the layout can change.</summary>
        private void UpdateRequiredShift(ShopMenu shop)
        {
            if (ActivePortrait == null || !Game1.options.showMerchantPortraits)
            {
                RequiredShift = 0;
                return;
            }

            // Start from the layout's computed shift, then stack the post-layout tweaks:
            // a fixed −7px cosmetic nudge, and a further −54px when the scrollbar option is on.
            int shift = ComputePortraitLayout(shop).Shift;
            shift -= 7;
            if (_config.ShowScrollbar)
                shift -= 54;

            RequiredShift = Math.Max(0, shift);
        }

        /// <summary>Computes the portrait's draw rectangle and the matching menu shift together,
        /// so the two are always consistent. When the portrait fits beside the menu the original
        /// layout is kept unchanged; when the window is too small, the menu is pinned flush-right
        /// and the portrait is scaled down to the space that leaves — so it can never overlap the
        /// menu, and the menu can never clip off the screen edge.</summary>
        private (Rectangle Rect, int Shift) ComputePortraitLayout(ShopMenu shop)
        {
            Texture2D texture = ActivePortrait;
            int vpW = Game1.uiViewport.Width;
            int vpH = Game1.uiViewport.Height;

            // An animated portrait's texture is a sprite sheet, so the on-screen aspect ratio
            // must come from a single frame, not the whole sheet.
            var anim = ActiveSettings?.Animation;
            float aspect = (anim != null && anim.FrameWidth > 0 && anim.FrameHeight > 0)
                ? (float)anim.FrameWidth / anim.FrameHeight
                : (float)texture.Width / texture.Height;

            // Natural (uncapped) portrait size.
            float scaleMultiplier = _config.PortraitScalePct / 100f;
            int natH = (int)(vpH * 0.80f * scaleMultiplier);
            int maxH = (int)(vpH * _config.MaxHeightFraction);
            if (natH > maxH)
                natH = maxH;
            int natW = (int)(natH * aspect);

            // Per-shop pack offsets override the global config; on Android an optional
            // AndroidOffsetX/Y takes precedence, then DefaultOffsetX/Y, then config.
            ShopPortraitSettings settings = ActiveSettings;
            bool isAndroid = Constants.TargetPlatform == StardewModdingAPI.GamePlatform.Android;
            int offsetX = isAndroid
                ? (settings?.AndroidOffsetX ?? settings?.DefaultOffsetX ?? _config.HorizontalOffset)
                : (settings?.DefaultOffsetX ?? _config.HorizontalOffset);
            int offsetY = isAndroid
                ? (settings?.AndroidOffsetY ?? settings?.DefaultOffsetY ?? _config.VerticalOffset)
                : (settings?.DefaultOffsetY ?? _config.VerticalOffset);

            // Natural position: centered within the left 40% of the screen (original layout).
            int leftRegion = (int)(vpW * 0.40f);
            int natX = (leftRegion - natW) / 2 + offsetX;
            int natY = vpH - natH + offsetY;

            // Android never shifts the menu, so keep the original uncapped layout there.
            if (isAndroid)
                return (new Rectangle(natX, natY, natW, natH), 0);

            int origX = shop.xPositionOnScreen;
            int menuW = shop.width;

            // The furthest the menu can shift right before it clips off the screen edge.
            int maxShift = Math.Max(0, vpW - menuW - origX);

            // Shift needed to clear the portrait at its natural position.
            int naturalShift = (natX + natW) + PortraitMenuGap - origX;

            if (naturalShift <= maxShift)
            {
                // The portrait fits beside the menu — keep the original layout untouched.
                return (new Rectangle(natX, natY, natW, natH), Math.Max(0, naturalShift));
            }

            // Not enough room: pin the menu flush-right and shrink the portrait to fit the
            // space that leaves, centered within it.
            int zoneW = Math.Max(1, origX + maxShift - PortraitMenuGap);
            int pW = natW;
            int pH = natH;
            if (pW > zoneW)
            {
                pW = zoneW;
                pH = (int)(pW / aspect);
            }
            int pX = (zoneW - pW) / 2 + offsetX;
            int pY = vpH - pH + offsetY;
            return (new Rectangle(pX, pY, pW, pH), maxShift);
        }

        private void ExportSettingsForPackAuthors()
        {
            var packs = GetPacks();
            var allShops = GetShops()
                .Concat(GetDesertFestivalShops())
                .Concat(GetMiscShops());

            var portraits = new Dictionary<string, object>();

            foreach (var (shopId, _) in allShops)
            {
                var packId = _config.ShopPreferences.GetValueOrDefault(shopId, "Auto");
                if (packId == "Auto" || string.IsNullOrEmpty(packId))
                    packId = GetDefaultPackId(shopId, packs);

                if (packId == "Vanilla" || packId == null)
                    continue;

                // Overlay pack-specific settings on top of global config values.
                ShopPortraitSettings packSettings = null;
                if (packs.TryGetValue(packId, out var pack) &&
                    pack.Portraits.TryGetValue(shopId, out var ps))
                    packSettings = ps;

                portraits[shopId] = new
                {
                    DefaultScale   = packSettings?.DefaultScale   ?? _config.PortraitScalePct,
                    DefaultOffsetX = packSettings?.DefaultOffsetX ?? _config.HorizontalOffset,
                    DefaultOffsetY = packSettings?.DefaultOffsetY ?? _config.VerticalOffset,
                    AndroidOffsetX = packSettings?.AndroidOffsetX ?? packSettings?.DefaultOffsetX ?? _config.HorizontalOffset,
                    AndroidOffsetY = packSettings?.AndroidOffsetY ?? packSettings?.DefaultOffsetY ?? _config.VerticalOffset,
                };
            }

            var output = new { Portraits = portraits };
            string json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
            string path = Path.Combine(Helper.DirectoryPath, "SPO_AuthorExport.json");
            File.WriteAllText(path, json);
            Monitor.Log($"[SPO] Export written to: {path}", LogLevel.Info);
        }

        private Texture2D GetPortraitForShop(string shopId)
        {
            var packId = _config.ShopPreferences.GetValueOrDefault(shopId, "Auto");

            // "Auto" (or missing) means use the first available pack.
            if (packId == "Auto" || string.IsNullOrEmpty(packId))
                packId = GetDefaultPackId(shopId, GetPacks());

            if (packId == "Vanilla" || packId == null) return null;

            try
            {
                return Helper.GameContent.Load<Texture2D>($"Custom/{packId}/ShopPortraits/{shopId}");
            }
            catch { return null; }
        }

        /// <summary>Resolves the active pack's per-shop settings using the same pack
        /// selection as <see cref="GetPortraitForShop"/>. Returns null when Vanilla is
        /// selected or the chosen pack has no per-shop entry, in which case the draw
        /// path falls back to the global config offsets.</summary>
        private ShopPortraitSettings GetSettingsForShop(string shopId)
        {
            var packs = GetPacks();
            var packId = _config.ShopPreferences.GetValueOrDefault(shopId, "Auto");

            if (packId == "Auto" || string.IsNullOrEmpty(packId))
                packId = GetDefaultPackId(shopId, packs);

            if (packId == "Vanilla" || packId == null) return null;

            if (packs.TryGetValue(packId, out var pack) &&
                pack.Portraits.TryGetValue(shopId, out var ps))
                return ps;

            return null;
        }

        /// <summary>Loads the packs dictionary fresh from SMAPI's content cache each call.
        /// SMAPI handles asset caching internally; InvalidateCache clears it when needed.</summary>
        private Dictionary<string, PackRegistration> GetPacks()
        {
            return Helper.GameContent
                .Load<Dictionary<string, PackRegistration>>("Custom/ShopPortraitOverhaul/Packs");
        }

        private static string[] BuildAllowedValues(string shopId, Dictionary<string, PackRegistration> packs)
        {
            var list = new List<string>();
            foreach (var (id, pack) in packs)
                if (pack.Portraits.ContainsKey(shopId))
                    list.Add(id);
            list.Add("Vanilla");
            return list.ToArray();
        }

        /// <summary>Returns neutral + all pack IDs + Vanilla for the master "Set All" dropdown.</summary>
        private static string[] BuildAllPackValues(string neutral, Dictionary<string, PackRegistration> packs)
        {
            var list = new List<string> { neutral };
            list.AddRange(packs.Keys);
            list.Add("Vanilla");
            return list.ToArray();
        }

        /// <summary>Returns the pack to use when no explicit preference is saved.
        /// If one or more packs have a portrait for this shop, use the first
        /// alphabetically. If no pack does, fall back to Vanilla.</summary>
        private static string GetDefaultPackId(string shopId, Dictionary<string, PackRegistration> packs)
        {
            var matches = packs
                .Where(kvp => kvp.Value.Portraits.ContainsKey(shopId))
                .Select(kvp => kvp.Key)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return matches.Count > 0 ? matches[0] : "Vanilla";
        }

        private static IEnumerable<(string ShopId, string DisplayName)> GetShops()
        {
            return new[]
            {
                ("SeedShop",            "Pierre's General Store"),
                ("Carpenter",           "Robin's Carpenter Shop"),
                ("AdventureShop",       "Marlon's Adventure Shop"),
                ("AdventureGuildRecovery", "Marlon's Item Recovery"),
                ("Blacksmith",          "Clint's Blacksmith"),
                ("ClintUpgrade",        "Clint's Tool Upgrades"),
                ("Saloon",              "Gus's Saloon"),
                ("ResortBar",           "Gus's Resort Bar"),
                ("AnimalShop",          "Marnie's Animal Shop"),
                ("PetAdoption",         "Marnie's Pet Adoption"),
                ("FishShop",            "Willy's Fish Shop"),
                ("Sandy",               "Sandy's Oasis"),
                ("ShadowShop",          "Krobus's Shop"),
                ("Hospital_Harvey",     "Harvey's Clinic"),
                ("Hospital_Maru",       "Maru's Clinic"),
                ("Dwarf",               "Dwarf's Shop"),
                ("IceCreamStand",       "Alex's Ice Cream Stand"),
            };
        }

        private static IEnumerable<(string ShopId, string DisplayName)> GetDesertFestivalShops()
        {
            return new[]
            {
                ("DesertFestival_Abigail",  "Desert Festival - Abigail"),
                ("DesertFestival_Alex",     "Desert Festival - Alex"),
                ("DesertFestival_Caroline", "Desert Festival - Caroline"),
                ("DesertFestival_Clint",    "Desert Festival - Clint"),
                ("DesertFestival_Demetrius","Desert Festival - Demetrius"),
                ("DesertFestival_Elliott",  "Desert Festival - Elliott"),
                ("DesertFestival_Emily",    "Desert Festival - Emily"),
                ("DesertFestival_Evelyn",   "Desert Festival - Evelyn"),
                ("DesertFestival_George",   "Desert Festival - George"),
                ("DesertFestival_Gus",      "Desert Festival - Gus"),
                ("DesertFestival_Haley",    "Desert Festival - Haley"),
                ("DesertFestival_Harvey",   "Desert Festival - Harvey"),
                ("DesertFestival_Jas",      "Desert Festival - Jas"),
                ("DesertFestival_Jodi",     "Desert Festival - Jodi"),
                ("DesertFestival_Kent",     "Desert Festival - Kent"),
                ("DesertFestival_Leah",     "Desert Festival - Leah"),
                ("DesertFestival_Leo",      "Desert Festival - Leo"),
                ("DesertFestival_Marnie",   "Desert Festival - Marnie"),
                ("DesertFestival_Maru",     "Desert Festival - Maru"),
                ("DesertFestival_Pam",      "Desert Festival - Pam"),
                ("DesertFestival_Penny",    "Desert Festival - Penny"),
                ("DesertFestival_Pierre",   "Desert Festival - Pierre"),
                ("DesertFestival_Robin",    "Desert Festival - Robin"),
                ("DesertFestival_Sam",      "Desert Festival - Sam"),
                ("DesertFestival_Sebastian","Desert Festival - Sebastian"),
                ("DesertFestival_Shane",    "Desert Festival - Shane"),
                ("DesertFestival_Vincent",  "Desert Festival - Vincent"),
            };
        }

        private static IEnumerable<(string ShopId, string DisplayName)> GetMiscShops()
        {
            return new[]
            {
                ("HatMouse",                                    "Hat Mouse"),
                ("Casino",                                      "Casino"),
                ("VolcanoShop",                                 "Volcano Shop"),
                ("DesertTrade",                                 "Desert Trader"),
                ("Traveler",                                    "Traveling Cart"),
                ("QiGemShop",                                   "Qi's Gem Shop"),
                ("IslandTrade",                                 "Island Trader"),
                ("BoxOffice",                                   "Movie Theater Box Office"),
                ("Raccoon",                                     "Raccoon Wife's Shop"),
                ("Joja",                                        "Joja Mart"),
                ("Bookseller",                                  "Bookseller"),
                ("BooksellerTrade",                             "Bookseller Trade"),
                ("DesertFestival_EggShop",                      "Desert Festival Egg Shop"),
                ("Festival_EggFestival_Pierre",                 "Egg Festival - Pierre"),
                ("Festival_FlowerDance_Pierre",                 "Flower Dance - Pierre"),
                ("Festival_Luau_Pierre",                        "Luau - Pierre"),
                ("Festival_DanceOfTheMoonlightJellies_Pierre",  "Dance of Moonlight Jellies - Pierre"),
                ("Festival_StardewValleyFair_StarTokens",       "Stardew Valley Fair"),
                ("Festival_SpiritsEve_Pierre",                  "Spirit's Eve - Pierre"),
                ("Festival_FestivalOfIce_TravelingMerchant",    "Festival of Ice - Traveling Merchant"),
                ("Festival_FeastOfTheWinterStar_Pierre",        "Feast of Winter Star - Pierre"),
                ("Festival_NightMarket_DecorationBoat",         "Night Market - Decoration Boat"),
                ("Festival_NightMarket_MagicBoat_Day1",         "Night Market - Magic Boat Day 1"),
                ("Festival_NightMarket_MagicBoat_Day2",         "Night Market - Magic Boat Day 2"),
                ("Festival_NightMarket_MagicBoat_Day3",         "Night Market - Magic Boat Day 3"),
            };
        }
    }
}
