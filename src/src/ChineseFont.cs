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
/// that ships next to the DLL and register it in every font asset's
/// <c>fallbackFontAssetTable</c>. The game's own fonts stay untouched — only
/// glyphs they are missing fall through to ours.
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

    private static ManualLogSource _log;
    private static TMP_FontAsset _font;
    private static bool _attempted;
    private static bool _applied;
    private static float _nextRefresh;
    private static int _lastRegistered;

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

        // Font assets arrive with their scenes, so keep sweeping until every
        // loaded font asset knows about the fallback.
        if (Time.unscaledTime < _nextRefresh)
            return;
        _nextRefresh = Time.unscaledTime + 2f;

        if (Register())
            RefreshRenderedText();
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
        _applied = true;
        RefreshRenderedText();
        _nextRefresh = Time.unscaledTime + 2f;
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
