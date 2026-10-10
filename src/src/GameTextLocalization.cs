using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using Object = UnityEngine.Object;
using UnityEngine;

namespace HvrChinese;

/// <summary>
/// Translates the text that is not in the game's string tables.
///
/// The string tables cover everything drawn through Unity Localization's
/// property variants, but a large part of the game's writing lives in
/// ScriptableObject data instead:
///
///   * <see cref="DialogScript"/> — the per-level dialogue. The game ships an
///     English and a Japanese script per level and picks the Japanese one inside
///     <c>if (locale == "ja")</c>, so Chinese always got the English script.
///   * <see cref="Cutscene"/> — the story cutscenes, same en/ja split.
///   * <see cref="Level.displayName"/> — "Story 1-1" etc.
///   * <see cref="TowerAlmanacEntry"/> — the Collection screen's summary,
///     description and flavour text, plus the toughness / recharge / placement
///     values that <see cref="AlmanacHandler.PrepareEntries"/> rebuilds in
///     hard-coded English every time the scene loads.
///
/// Dialogue and cutscenes are substituted where they are drawn, so they work no
/// matter when the asset was loaded. The rest is rewritten on the loaded assets
/// themselves, with the English kept so switching back restores it.
/// </summary>
internal static class GameTextLocalization
{
    private static readonly Regex EntryRegex = new Regex(
        @"\{\s*""key""\s*:\s*""((?:[^""\\]|\\.)*)""\s*,\s*""value""\s*:\s*""((?:[^""\\]|\\.)*)""\s*\}");

    private static readonly Regex StoryNameRegex = new Regex(@"^Story\s+(.+)$");
    private const string RechargeSuffix = " seconds";

    private static readonly Dictionary<string, string> Dialogue = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> Almanac = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> Misc = new Dictionary<string, string>();

    private static ManualLogSource _log;
    private static bool _loaded;
    private static bool _applied;
    private static float _nextRefresh;
    private static float _nextTextRefresh;

    /// <summary>English text of every asset field we rewrote, keyed by field.</summary>
    private sealed class Original
    {
        public string LevelName;
        public string Summary;
        public string Description;
        public string FlavorText;
        public string Damage;
        public string AtkSpd;
        public string Toughness;
        public string Placement;
        public string Recharge;
        public string AchievementName;
        public string AchievementDescription;
        public string ShopItem;
        public string ShopDescription;
    }

    private static readonly Dictionary<Object, Original> Originals =
        new Dictionary<Object, Original>();

    public static void Initialise(ManualLogSource log) => _log = log;

    public static void Tick()
    {
        Load();

        var wantChinese = ChineseLocalization.IsActive;
        if (wantChinese != _applied)
        {
            _applied = wantChinese;
            if (wantChinese)
                Apply();
            else
                Restore();
            return;
        }

        if (!wantChinese)
            return;

        // Assets arrive with their scenes: the shop items only exist once the shop
        // is open, the collection entries once the Collection screen is. Re-scan
        // so anything that showed up later gets translated too.
        if (Time.unscaledTime < _nextRefresh)
            return;
        _nextRefresh = Time.unscaledTime + 2f;

        Apply();

        // Text the game writes into TextMeshPro components from code (the Marathon
        // interlude screen, the survival "current wave" label, the Marathon
        // tutorial) is not in any table and has no localizer, so it is swapped
        // here. Runs more often than the asset sweep because those strings are
        // assigned when a scene opens.
        if (Time.unscaledTime < _nextTextRefresh)
            return;
        _nextTextRefresh = Time.unscaledTime + 0.5f;

        ApplyUiTexts();
    }

    // ------------------------------------------------------------------ loading

    private static void Load()
    {
        if (_loaded)
            return;
        _loaded = true;

        var assembly = Assembly.GetExecutingAssembly();

        var dialogue = ReadResource(assembly, "HvrChinese.dialogue.zh.json");
        if (dialogue == null)
            _log.LogError("embedded resource HvrChinese.dialogue.zh.json not found");
        else
            foreach (Match m in EntryRegex.Matches(dialogue))
                Dialogue[Unescape(m.Groups[1].Value)] = Unescape(m.Groups[2].Value);

        var almanac = ReadResource(assembly, "HvrChinese.almanac.zh.json");
        if (almanac == null)
            _log.LogError("embedded resource HvrChinese.almanac.zh.json not found");
        else
            foreach (Match m in EntryRegex.Matches(almanac))
                Almanac[Unescape(m.Groups[1].Value)] = Unescape(m.Groups[2].Value);

        var misc = ReadResource(assembly, "HvrChinese.misc.zh.json");
        if (misc == null)
            _log.LogError("embedded resource HvrChinese.misc.zh.json not found");
        else
            foreach (Match m in EntryRegex.Matches(misc))
                Misc[Unescape(m.Groups[1].Value)] = Unescape(m.Groups[2].Value);

        _log.LogInfo($"loaded {Dialogue.Count} dialogue lines, {Almanac.Count} collection " +
                     $"strings and {Misc.Count} achievement/shop strings");
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0)
            return text;

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                builder.Append(c);
                continue;
            }

            i++;
            switch (text[i])
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                default: builder.Append(text[i]); break;
            }
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------ applying

    private static void Apply()
    {
        var levels = 0;

        foreach (var level in Resources.FindObjectsOfTypeAll<Level>())
        {
            if (level == null || string.IsNullOrEmpty(level.displayName))
                continue;

            var saved = Saved(level);
            var translated = LevelName(level.displayName);
            if (translated == null)
                continue;

            if (Field(ref saved.LevelName, level.displayName,
                      v => level.displayName = v, translated))
                levels++;
        }

        // Only the English entries: the Japanese ones belong to the game and are
        // shown as-is whenever the locale is "ja".
        var entries = AlmanacHandler.instance != null
            ? ApplyToEntries(AlmanacHandler.instance.almanacEntries)
            : 0;

        var achievements = 0;
        foreach (var info in Resources.FindObjectsOfTypeAll<AchievementInfo>())
        {
            if (info == null)
                continue;

            var saved = Saved(info);
            achievements += One(Field(ref saved.AchievementName, info.displayName,
                v => info.displayName = v,
                Lookup(Misc, $"achievement.name.{(int)info.achievementName}")));
            achievements += One(Field(ref saved.AchievementDescription, info.description,
                v => info.description = v,
                Lookup(Misc, $"achievement.description.{(int)info.achievementName}")));
        }

        var shopItems = 0;
        foreach (var item in Resources.FindObjectsOfTypeAll<ShopItem>())
        {
            if (item == null)
                continue;

            var saved = Saved(item);
            shopItems += One(Field(ref saved.ShopItem, item.itemName,
                v => item.itemName = v,
                Lookup(Misc, $"shop.item.{item.itemName}")));
            shopItems += One(Field(ref saved.ShopDescription, item.itemDescription,
                v => item.itemDescription = v,
                Lookup(Misc, $"shop.description.{item.itemDescription}")));
        }

        if (levels > 0 || entries > 0 || achievements > 0 || shopItems > 0)
            _log.LogInfo($"translated {levels} level name(s), {entries} collection entries, " +
                         $"{achievements} achievement field(s) and {shopItems} shop field(s)");
    }

    private static int ApplyToEntries(IEnumerable<TowerAlmanacEntry> entries)
    {
        if (entries == null)
            return 0;

        var count = 0;
        foreach (var entry in entries)
        {
            if (entry != null && TranslateEntry(entry))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Text the game assigns to a TextMeshPro component from code, with the
    /// English it held so leaving Chinese puts it back.
    /// </summary>
    private static readonly Dictionary<TMP_Text, string> OriginalUiTexts =
        new Dictionary<TMP_Text, string>();

    /// <summary>Strings the game builds with a number in them.</summary>
    private static readonly List<KeyValuePair<Regex, string>> UiTextPatterns =
        new List<KeyValuePair<Regex, string>>
        {
            new KeyValuePair<Regex, string>(new Regex(@"^Current wave: (\d+)$"), "当前波次：$1"),
            new KeyValuePair<Regex, string>(new Regex(@"^Survived for (\d+) waves!$"), "已存活 $1 波！"),
        };

    private static void ApplyUiTexts()
    {
        foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (text == null || string.IsNullOrEmpty(text.text))
                continue;

            var translated = TranslateUiText(text.text);
            if (translated == null || translated == text.text)
                continue;

            if (!OriginalUiTexts.ContainsKey(text))
                OriginalUiTexts[text] = text.text;
            text.text = translated;
        }
    }

    private static string TranslateUiText(string value)
    {
        if (Misc.TryGetValue("ui.text." + value, out var exact))
            return exact;

        foreach (var pattern in UiTextPatterns)
        {
            var match = pattern.Key.Match(value);
            if (match.Success)
                return match.Result(pattern.Value);
        }

        return null;
    }

    private static void RestoreUiTexts()
    {
        foreach (var pair in OriginalUiTexts)
        {
            if (pair.Key != null)
                pair.Key.text = pair.Value;
        }
        OriginalUiTexts.Clear();
    }

    private static void Restore()
    {
        foreach (var pair in Originals)
        {
            var target = pair.Key;
            if (target == null)
                continue;
            var original = pair.Value;

            switch (target)
            {
                case Level level:
                    if (original.LevelName != null)
                        level.displayName = original.LevelName;
                    break;
                case TowerAlmanacEntry entry:
                    if (original.Summary != null) entry.summary = original.Summary;
                    if (original.Description != null) entry.description = original.Description;
                    if (original.FlavorText != null) entry.flavorText = original.FlavorText;
                    if (original.Damage != null) entry.damage = original.Damage;
                    if (original.AtkSpd != null) entry.atkSpd = original.AtkSpd;
                    if (original.Toughness != null) entry.toughness = original.Toughness;
                    if (original.Placement != null) entry.placementType = original.Placement;
                    if (original.Recharge != null) entry.recharge = original.Recharge;
                    break;
                case AchievementInfo info:
                    if (original.AchievementName != null) info.displayName = original.AchievementName;
                    if (original.AchievementDescription != null) info.description = original.AchievementDescription;
                    break;
                case ShopItem item:
                    if (original.ShopItem != null) item.itemName = original.ShopItem;
                    if (original.ShopDescription != null) item.itemDescription = original.ShopDescription;
                    break;
            }
        }

        Originals.Clear();
        RestoreUiTexts();
    }

    private static bool TranslateEntry(TowerAlmanacEntry entry)
    {
        var saved = Saved(entry);
        var id = (int)entry.towerName;
        var changed = false;

        changed |= Field(ref saved.Summary, entry.summary,
            v => entry.summary = v, Lookup(Almanac, $"almanac.summary.{id}"));

        changed |= Field(ref saved.Description, entry.description,
            v => entry.description = v, Lookup(Almanac, $"almanac.description.{id}"));

        changed |= Field(ref saved.FlavorText, entry.flavorText,
            v => entry.flavorText = v, Lookup(Almanac, $"almanac.flavorText.{id}"));

        // The Collection screen's damage and attack-speed ratings: a closed set of
        // English words ("Low", "Massive", "Single Use", "Unlimited", …) that the
        // game stores per tower.
        changed |= Field(ref saved.Damage, entry.damage,
            v => entry.damage = v, Lookup(Almanac, $"almanac.damage.{entry.damage}"));

        changed |= Field(ref saved.AtkSpd, entry.atkSpd,
            v => entry.atkSpd = v, Lookup(Almanac, $"almanac.atkSpd.{entry.atkSpd}"));

        // PrepareEntries rebuilds these three in hard-coded English every time the
        // Collection scene opens, so they are translated here too; the postfix on
        // PrepareEntries re-applies it after each rebuild.
        changed |= Field(ref saved.Toughness, entry.toughness,
            v => entry.toughness = v,
            Lookup(Almanac, $"almanac.toughness.{entry.toughness}"));

        changed |= Field(ref saved.Placement, entry.placementType,
            v => entry.placementType = v,
            Lookup(Almanac, $"almanac.placementType.{entry.placementType}"));

        string recharge = null;
        if (entry.recharge != null && entry.recharge.EndsWith(RechargeSuffix) &&
            Almanac.TryGetValue("almanac.recharge.seconds", out var suffix))
        {
            recharge = entry.recharge.Substring(0, entry.recharge.Length - RechargeSuffix.Length)
                       + " " + suffix;
        }

        changed |= Field(ref saved.Recharge, entry.recharge,
            v => entry.recharge = v, recharge);

        return changed;
    }

    private static string Lookup(Dictionary<string, string> table, string key) =>
        table.TryGetValue(key, out var value) ? value : null;

    private static int One(bool changed) => changed ? 1 : 0;

    private static Original Saved(Object target)
    {
        if (!Originals.TryGetValue(target, out var saved))
        {
            saved = new Original();
            Originals[target] = saved;
        }
        return saved;
    }

    /// <summary>
    /// Applies one translated value, recording the English on first sight.
    ///
    /// The saved value is only written once, so re-running this after the game has
    /// rebuilt the field in English never stores Chinese as the "original". And
    /// because it compares against the value that is there now, re-running it on
    /// an already-translated asset is a no-op.
    /// </summary>
    private static bool Field(ref string savedOriginal, string current,
                              Action<string> set, string translated)
    {
        if (translated == null || current == translated)
            return false;

        savedOriginal ??= current;
        set(translated);
        return true;
    }

    /// <summary>"Story 1-1" → "剧情 1-1", "Story FINAL" → "剧情 终章".</summary>
    private static string LevelName(string displayName)
    {
        var match = StoryNameRegex.Match(displayName);
        if (!match.Success)
            return null;

        var rest = match.Groups[1].Value.Trim();
        if (rest.Equals("FINAL", StringComparison.OrdinalIgnoreCase))
            rest = "终章";
        return "剧情 " + rest;
    }

    // ------------------------------------------------------------------ patches

    /// <summary>
    /// The dialogue bubbles. The game reads <c>DialogStep.speech</c> straight out
    /// of the English script asset, so the Chinese text is substituted where it is
    /// drawn — which also covers scripts that were only just loaded.
    /// </summary>
    [HarmonyPatch(typeof(DialogManager))]
    private static class DialogManagerPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(DialogManager.UpdateDisplay))]
        [HarmonyPatch(nameof(DialogManager.UpdateDisplayOneshot))]
        private static void Postfix(DialogManager __instance)
        {
            if (!ChineseLocalization.IsActive)
                return;

            Translate(__instance.speech);
            Translate(__instance.speechRight);
        }

        private static void Translate(TextMeshProUGUI field)
        {
            if (field == null || string.IsNullOrEmpty(field.text))
                return;
            if (Dialogue.TryGetValue(field.text, out var translated))
                field.text = translated;
        }
    }

    /// <summary>The story cutscenes, which show <c>CutsceneStep.text</c> directly.</summary>
    [HarmonyPatch(typeof(CutsceneHandler))]
    private static class CutsceneHandlerPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(CutsceneHandler.PlayStep))]
        [HarmonyPatch(nameof(CutsceneHandler.Skip))]
        private static void Postfix(CutsceneHandler __instance)
        {
            if (!ChineseLocalization.IsActive || __instance.text == null)
                return;
            if (Dialogue.TryGetValue(__instance.text.text, out var translated))
                __instance.text.text = translated;
        }
    }

    /// <summary>
    /// <see cref="AlmanacHandler.PrepareEntries"/> rebuilds the Collection
    /// entries' toughness / recharge / placement values in hard-coded English
    /// every time the scene opens, so the translation has to be re-applied after
    /// each rebuild.
    /// </summary>
    [HarmonyPatch(typeof(AlmanacHandler))]
    private static class AlmanacHandlerPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(AlmanacHandler.PrepareEntries))]
        private static void Postfix(AlmanacHandler __instance)
        {
            if (!ChineseLocalization.IsActive)
                return;

            ApplyToEntries(__instance.almanacEntries);
        }
    }

    /// <summary>
    /// Translates the entry before it is drawn, so switching to Chinese with F7
    /// while the Collection screen is open updates what is on screen.
    /// </summary>
    [HarmonyPatch(typeof(AlmanacDisplay))]
    private static class AlmanacDisplayPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AlmanacDisplay.UpdateDisplay))]
        private static void Prefix(TowerAlmanacEntry preview)
        {
            if (!ChineseLocalization.IsActive)
                return;

            if (preview != null)
                TranslateEntry(preview);
        }
    }

    /// <summary>
    /// The shop's purchase confirmation is built with a hard-coded English
    /// sentence (and a special one for the Gigi standee).
    /// </summary>
    [HarmonyPatch(typeof(ShopManager))]
    private static class ShopManagerPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ShopManager.ConfirmPurchase))]
        private static void Postfix(ShopManager __instance)
        {
            if (!ChineseLocalization.IsActive)
                return;
            if (__instance.purchaseConfirmText == null || __instance.currentItem == null)
                return;

            var item = __instance.currentItem;
            var name = Lookup(Misc, $"shop.item.{item.itemName}") ?? item.itemName;

            var gigi = item.itemName == "Gigi";
            var template = Lookup(Misc, gigi ? "shop.confirmGigi" : "shop.confirm");
            if (template == null)
                return;

            __instance.purchaseConfirmText.text = gigi
                ? string.Format(template, item.price)
                : string.Format(template, name, item.price);
        }
    }
}
