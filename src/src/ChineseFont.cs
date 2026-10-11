using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.TextCore.LowLevel;

namespace HvrChinese;

/// <summary>
/// Gives the game's TextMeshPro fonts full Chinese coverage.
///
/// The game's own font assets (`nagino SDF`, `KOT-永代ゴシック-Bold SDF`) are
/// <c>AtlasPopulationMode.Dynamic</c> with a <c>sourceFontFile</c>, so they
/// rasterise whatever they are asked for on demand. Their source fonts are
/// Japanese, which means simplified-only Hanzi (门, 关, 击, 镜, 马 …) are missing
/// and render as boxes while the characters Japanese happens to share with
/// simplified Chinese (中, 文, 的 …) render fine.
///
/// The fix: build a <see cref="TMP_FontAsset"/> from the subsetted Noto Sans SC
/// that ships next to the DLL and put it on every TextMeshPro component while
/// Chinese is selected, so all glyphs come from one typeface.
///
/// Only using it as a *fallback* is not enough: the game's Japanese fonts share
/// most Hanzi with simplified Chinese, so 中/文/的 would still be drawn in the
/// Japanese typeface while 镜/马 fell through to Noto Sans SC — a mix of two
/// typefaces in one line. The fallback list is still registered (it covers
/// components that appear between sweeps), but the font itself is swapped. TMP's
/// <c>font</c> setter re-assigns the material from the new font, and the
/// originals are kept so switching back restores them.
///
/// Two things this has to get right:
///
///   * The font asset must be built with the **file path** overload of
///     <see cref="TMP_FontAsset.CreateFontAsset(string,int,int,GlyphRenderMode,int,int)"/>.
///     The <c>Font</c>-based overload loads the face through the OS font table,
///     which needs the font to be installed with the OS (and a process-private
///     <c>AddFontResourceEx</c> registration is not visible to Unity's font
///     engine), so it returns null at run time. The file overload reads the file
///     directly and always works.
///   * Font assets are loaded per scene, so the fallback has to be re-registered
///     as new font assets appear — a single pass at start-up misses everything
///     the Collection, Level Select and in-game scenes load later.
/// </summary>
internal static class ChineseFont
{
    private const string FontFileName = "NotoSansSC-subset.otf";

    /// <summary>Never advance a line by less than this many em.</summary>
    private const float MinLineAdvanceRatio = 1.0f;

    private static ManualLogSource _log;
    private static TMP_FontAsset _font;
    private static bool _attempted;
    private static bool _applied;
    private static float _nextRefresh;
    private static int _lastRegistered;
    private static string _activeScene;

    /// <summary>What a TextMeshPro component used before we swapped the font.</summary>
    private sealed class Watched
    {
        public TMP_FontAsset Font;
        public Material Material;
        public Material Clone;
        public float LineSpacing;
    }

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

    private static readonly Dictionary<TMP_Text, Watched> WatchedTexts =
        new Dictionary<TMP_Text, Watched>();

    public static void Initialise(ManualLogSource log) => _log = log;

    /// <summary>True once the game's fonts can render every glyph we ship.</summary>
    public static bool Applied => _applied;

    /// <summary>Creates the font asset once, then keeps the fallback lists fresh.</summary>
    public static void Tick()
    {
        if (_font == null)
        {
            if (_attempted)
                return;
            _attempted = true;
            _font = Create();
            if (_font == null)
                return;
        }

        // Only while Chinese is selected: the fallback exists to fill in the
        // glyphs the game's own fonts cannot rasterise, and English/Japanese
        // never ask for them.
        var wantChinese = ChineseLocalization.IsActive;
        if (wantChinese != _applied)
        {
            if (wantChinese)
                Apply();
            else
                Restore();
            return;
        }

        if (!wantChinese)
            return;

        // The game re-assigns fonts from its per-locale asset table, so a swapped
        // text can be put back on the game's own font at any time.
        ReassertFonts();

        // Font assets and text components arrive with their scenes, so keep
        // sweeping until every one of them has been seen. The font has to be
        // re-swapped here, not just the fallback: a scene that loaded after the
        // initial swap would otherwise keep the game's own font and only fall
        // through for the glyphs it lacks — which is the mixed typography.
        if (Time.unscaledTime < _nextRefresh && _activeScene == ActiveScene)
            return;
        _nextRefresh = Time.unscaledTime + 2f;
        _activeScene = ActiveScene;

        SwapFonts();
        if (Register())
            RefreshRenderedText();
    }

    /// <summary>Active scene name, so a scene change can force an immediate sweep.</summary>
    private static string ActiveScene
    {
        get
        {
            try
            {
                return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            }
            catch
            {
                return null;
            }
        }
    }

    private static TMP_FontAsset Create()
    {
        var path = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
            FontFileName);

        if (!File.Exists(path))
        {
            _log.LogWarning($"font file not found next to the plugin: {path}");
            return null;
        }

        try
        {
            // faceIndex 0, 90 pt sampling, 9 px padding, SDFAA rendering into
            // 1024x1024 atlases. The subset is ~1.5k glyphs, so this is quick.
            var font = TMP_FontAsset.CreateFontAsset(
                path, 0, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024);

            if (font == null)
            {
                _log.LogWarning("TMP_FontAsset.CreateFontAsset returned null");
                return null;
            }

            font.name = "Noto Sans SC (Chinese fallback)";
            _log.LogInfo($"created a TMP font asset from {FontFileName}");
            return font;
        }
        catch (Exception error)
        {
            _log.LogWarning("could not create the Chinese font asset: " + error.Message);
            return null;
        }
    }

    /// <summary>
    /// Adds the fallback to every loaded font asset. Returns true when a new
    /// font asset was covered, i.e. when already-rendered text may now show
    /// boxes that the fallback can fill in.
    /// </summary>
    private static bool Register()
    {
        var added = 0;

        foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (asset == null || asset == _font)
                continue;

            if (asset.fallbackFontAssetTable == null)
                asset.fallbackFontAssetTable = new List<TMP_FontAsset>();

            if (!asset.fallbackFontAssetTable.Contains(_font))
            {
                asset.fallbackFontAssetTable.Add(_font);
                added++;
            }
        }

        if (added > 0)
        {
            _lastRegistered += added;
            _log.LogInfo($"registered the Chinese fallback on {added} more font asset(s) " +
                         $"({_lastRegistered} total)");
        }

        return added > 0;
    }

    private static void Apply()
    {
        Register();
        SwapFonts();
        _applied = true;
        RefreshRenderedText();
        _nextRefresh = Time.unscaledTime + 2f;
    }

    /// <summary>
    /// Puts our font on every TextMeshPro component, remembering what each one
    /// used. Assigning <c>font</c> makes TMP load the matching material, so the
    /// original material is kept as well and restored on the way out.
    /// </summary>
    private static void SwapFonts()
    {
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || text.font == _font)
                continue;

            if (!WatchedTexts.ContainsKey(text))
                WatchedTexts[text] = new Watched
                {
                    Font = text.font,
                    Material = text.fontSharedMaterial,
                    LineSpacing = text.lineSpacing
                };

            text.font = _font;
            CompensateLineSpacing(text, WatchedTexts[text]);

            // Assigning the font makes TMP replace the material with the new
            // font's own, which throws away the game's look — the SOLD labels'
            // black outline and the shop speech's glow are all material
            // properties. Clone the original material instead (same shader, same
            // outline/glow/dilate values) and only repoint its atlas at ours, so
            // the design survives. TMP derives the per-atlas materials from this
            // one, so multi-atlas glyphs keep the look too.
            var watched = WatchedTexts[text];
            if (watched.Material != null && watched.Material.shader != null)
            {
                var material = new Material(watched.Material) { name = watched.Material.name + " (zh)" };
                material.SetTexture(MainTexId, _font.atlasTexture);
                material.SetFloat("_TextureWidth", _font.atlasWidth);
                material.SetFloat("_TextureHeight", _font.atlasHeight);

                text.fontSharedMaterial = material;
                watched.Clone = material;
            }
        }

        PruneDestroyed();
    }

    /// <summary>Text components come and go with scenes; drop the dead entries.</summary>
    private static void PruneDestroyed()
    {
        if (WatchedTexts.Count < 64)
            return;

        var dead = new List<TMP_Text>();
        foreach (var pair in WatchedTexts)
        {
            if (pair.Key == null)
                dead.Add(pair.Key);
        }
        foreach (var key in dead)
            WatchedTexts.Remove(key);
    }

    /// <summary>
    /// Keeps the line advance the game designed for after the font changed.
    ///
    /// TMP advances a line by <c>faceInfo.ascentLine - faceInfo.descentLine</c>
    /// plus <c>lineSpacing × fontSize × 0.01</c>. The game tunes that second term
    /// against its own font's line height — the speech bubble's font (BlueBubbles)
    /// has a 2.68 em line height and uses <c>lineSpacing = -200</c> to pull the
    /// lines back together. Noto Sans SC is only ~1.45 em, so the same -200
    /// subtracts more than the advance is worth and every line is drawn on top of
    /// the previous one, from the bottom up. Shifting lineSpacing by the
    /// difference of the two ratios keeps the advance exactly as intended.
    /// </summary>
    private static void CompensateLineSpacing(TMP_Text text, Watched watched)
    {
        if (watched.Font == null)
            return;

        var original = watched.Font.faceInfo;
        var ours = _font.faceInfo;
        if (original.pointSize <= 0f || ours.pointSize <= 0f)
            return;

        float gameRatio = (original.ascentLine - original.descentLine) / original.pointSize;
        float ourRatio = (ours.ascentLine - ours.descentLine) / ours.pointSize;

        // lineSpacing is in units of 1% of the font size.
        float shift = (gameRatio - ourRatio) * 100f;
        float compensated = watched.LineSpacing + shift;

        // Safety net: never let the advance go non-positive, however the original
        // font and lineSpacing combined. Lines drawn on top of each other are
        // unreadable, and a text whose height cannot be measured also defeats the
        // auto-sizing that is supposed to shrink it.
        float floor = (MinLineAdvanceRatio - ourRatio) * 100f;
        if (compensated < floor)
            compensated = floor;

        if (Mathf.Approximately(compensated, text.lineSpacing))
            return;

        text.lineSpacing = compensated;
        _log.LogInfo($"line spacing {watched.LineSpacing:F0} -> {compensated:F0} for " +
                     $"{text.gameObject.name} (line height {gameRatio:F2} -> {ourRatio:F2} em)");
    }

    /// <summary>Undoes the game putting its own font back on a swapped text.</summary>
    private static void ReassertFonts()
    {
        if (WatchedTexts.Count == 0)
            return;

        var reverted = 0;
        foreach (var pair in WatchedTexts)
        {
            var text = pair.Key;
            if (text == null)
                continue;
            if (text.font == _font)
                continue;

            text.font = _font;
            CompensateLineSpacing(text, pair.Value);
            reverted++;
        }

        if (reverted > 0)
            _log.LogInfo($"re-applied the Chinese font to {reverted} text component(s)");
    }

    private static void Restore()
    {
        foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (asset == null)
                continue;
            if (asset.fallbackFontAssetTable != null &&
                asset.fallbackFontAssetTable.Remove(_font))
            {
                asset.fallbackFontAssetTable.RemoveAll(f => f == null);
            }
        }

        foreach (var pair in WatchedTexts)
        {
            var text = pair.Key;
            if (text == null)
                continue;

            var watched = pair.Value;
            if (watched.Font != null && text.font != watched.Font)
                text.font = watched.Font;
            if (watched.Material != null && text.fontSharedMaterial != watched.Material)
                text.fontSharedMaterial = watched.Material;
            if (text.lineSpacing != watched.LineSpacing)
                text.lineSpacing = watched.LineSpacing;
            if (watched.Clone != null)
                UnityEngine.Object.Destroy(watched.Clone);
        }
        WatchedTexts.Clear();

        _lastRegistered = 0;
        _applied = false;
        RefreshRenderedText();
    }

    /// <summary>
    /// TMP only re-rasterises a text object when it is dirtied, and the fallback
    /// is usually attached *after* the locale change already set the text — so
    /// text that was drawn with boxes has to be asked to redraw itself.
    /// </summary>
    private static void RefreshRenderedText()
    {
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null)
                continue;
            try
            {
                text.SetAllDirty();
            }
            catch
            {
                // A destroyed or prefab object; nothing to refresh.
            }
        }
    }
}
