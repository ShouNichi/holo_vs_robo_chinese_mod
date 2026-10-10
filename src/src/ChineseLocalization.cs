using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;
using Locale = UnityEngine.Localization.Locale;

namespace HvrChinese;

/// <summary>
/// Puts the game into Chinese without giving it a locale it cannot serve.
///
/// Why not just register a "zh" locale? Because the game resolves every
/// *localized asset* — sprite, font, material — through the asset table for the
/// selected locale, and it ships asset tables for English and Japanese only. A
/// real "zh" locale therefore has to be served the English asset table from a
/// custom <see cref="ITableProvider"/>, and that provider has to load the English
/// table on demand *from inside* another table load — a nested
/// <c>WaitForCompletion</c> that only succeeds when the English bundle happens to
/// be loaded already. That is why the main-menu buttons came back as white boxes
/// on most launches and only worked after the game had been in English once.
///
/// So the Chinese locale keeps the **English locale code**. Everything that is
/// looked up by locale code (the asset tables, and therefore all artwork) then
/// travels the exact code path the game uses in English mode, with no plugin in
/// the way. Only the string table is intercepted: while Chinese is selected, the
/// game's own English locale is served our translated <see cref="StringTable"/>
/// instead of the English one.
///
/// The locale object is still distinct from the game's English locale (different
/// name), so selecting it is a real locale change and every
/// <see cref="GameObjectLocalizer"/> re-applies its variants — which is what makes
/// the UI switch to Chinese.
/// </summary>
internal static class ChineseLocalization
{
    public const string Code = "zh";
    public const string LocaleName = "中文 (zh)";
    private const string StringTableCollection = "Localization Strings";

    private static ManualLogSource _log;

    /// <summary>The game's own English locale, used as the template.</summary>
    private static Locale _baseLocale;

    /// <summary>
    /// Our locale: a clone of the English one that still carries the English
    /// locale code, so asset lookups stay on the game's own English path.
    /// </summary>
    private static Locale _locale;

    /// <summary>True while the game should be showing Chinese.</summary>
    private static bool _chineseMode;

    private static readonly Dictionary<string, string> Strings = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> EnumNames = new Dictionary<string, string>();
    private static bool _patched;
    private static float _nextPatchAttempt;
    private static float _nextInstallAttempt;

    /// <summary>
    /// Set once the player picks a language from the game's own settings screen
    /// (or with F7). After that the mod stops re-asserting its own choice, so an
    /// explicit choice is never fought.
    /// </summary>
    private static bool _userChoseLanguage;

    /// <summary>Language to select on start-up: "zh", "en" or "ja".</summary>
    public static ConfigEntry<string> StartLanguage { get; private set; }

    /// <summary>Key that cycles English -> Japanese -> Chinese.</summary>
    public static ConfigEntry<KeyCode> CycleKey { get; private set; }

    public static void Initialise(ManualLogSource log, ConfigFile config)
    {
        _log = log;

        // Every step is logged and guarded: this runs inside the BepInEx
        // chainloader, where an exception is reported as a bare
        // NullReferenceException with no stack and no way to tell which of the
        // half-dozen things in here failed.
        try
        {
            StartLanguage = config.Bind("General", "Language", "zh",
                "Language the game starts in: zh (Chinese), en (English) or ja (Japanese).");
            CycleKey = config.Bind("General", "CycleLanguageKey", KeyCode.F7,
                "Press in game to cycle English -> Japanese -> Chinese.");
            _log.LogInfo("config bound");
        }
        catch (Exception error)
        {
            _log.LogError("could not bind config: " + error);
        }

        LoadTranslations();
        _log.LogInfo($"loaded {Strings.Count} UI strings and {EnumNames.Count} enum names");
    }

    /// <summary>True when the game is currently showing Chinese.</summary>
    public static bool IsActive => _chineseMode;

    /// <summary>Patches the game. Retried until it succeeds.</summary>
    private static void TryPatch()
    {
        if (_patched)
            return;

        try
        {
            Harmony.CreateAndPatchAll(typeof(Plugin).Assembly);
            _patched = true;
            _log.LogInfo("patched EnumToString.String, MainManager.SetLanguage and friends");
        }
        catch (Exception error)
        {
            // Assembly-CSharp not loaded yet, or a target moved. Back off and
            // try again rather than dying.
            _log.LogWarning("patching failed, will retry: " + error.Message);
            _nextPatchAttempt = Time.unscaledTime + 2f;
        }
    }

    // ------------------------------------------------------------------ loading

    private static readonly System.Text.RegularExpressions.Regex StringEntryRegex =
        new System.Text.RegularExpressions.Regex(
            @"\{\s*""key""\s*:\s*""((?:[^""\\]|\\.)*)""\s*,\s*""value""\s*:\s*""((?:[^""\\]|\\.)*)""\s*\}");

    private static readonly System.Text.RegularExpressions.Regex EnumEntryRegex =
        new System.Text.RegularExpressions.Regex(
            @"\{\s*""enum""\s*:\s*""([^""]*)""\s*,\s*""name""\s*:\s*""([^""]*)""\s*,\s*""value""\s*:\s*""((?:[^""\\]|\\.)*)""\s*\}");

    /// <summary>
    /// Reads the embedded translation JSON.
    ///
    /// Parsed by hand rather than with <c>JsonUtility</c>: the file is generated
    /// by this project so its shape is known, and JsonUtility silently returns an
    /// object with null fields when it does not like a type - which surfaces as a
    /// NullReferenceException with no clue as to why.
    /// </summary>
    private static void LoadTranslations()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var stringsJson = ReadResource(assembly, "HvrChinese.strings.zh.json");
        if (stringsJson == null)
        {
            _log.LogError("embedded resource HvrChinese.strings.zh.json not found");
        }
        else
        {
            foreach (var match in StringEntryRegex.Matches(stringsJson).OfType<System.Text.RegularExpressions.Match>())
            {
                var key = Unescape(match.Groups[1].Value);
                if (!string.IsNullOrEmpty(key))
                    Strings[key] = Unescape(match.Groups[2].Value);
            }
        }

        var enumsJson = ReadResource(assembly, "HvrChinese.enums.zh.json");
        if (enumsJson == null)
        {
            _log.LogError("embedded resource HvrChinese.enums.zh.json not found");
        }
        else
        {
            foreach (var match in EnumEntryRegex.Matches(enumsJson).OfType<System.Text.RegularExpressions.Match>())
            {
                var key = match.Groups[1].Value + "." + match.Groups[2].Value;
                EnumNames[key] = Unescape(match.Groups[3].Value);
            }
        }
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

    // ----------------------------------------------------------------- install

    /// <summary>Called every frame: keeps the Chinese mode installed and selected.</summary>
    public static void Tick()
    {
        if (CycleKey != null && Input.GetKeyDown(CycleKey.Value))
            CycleLanguage();

        if (!_patched && Time.unscaledTime >= _nextPatchAttempt)
            TryPatch();

        if (Time.unscaledTime >= _nextInstallAttempt)
        {
            _nextInstallAttempt = Time.unscaledTime + 1f;
            try
            {
                EnsureInstalled();
            }
            catch (Exception error)
            {
                // The string database may not have finished loading its tables yet,
                // so keep trying rather than giving up for the whole session.
                _log.LogWarning("locale install not ready, will retry: " + error.Message);
            }
        }
    }

    /// <summary>
    /// Makes sure the string table provider is ours and Chinese is selected.
    ///
    /// Runs repeatedly because Unity Localization can drop the provider: the
    /// settings object is re-created when its host object is destroyed by a scene
    /// transition, and a re-created settings object has no provider wrapper —
    /// which is how the game used to end up back in English after coming out of a
    /// level.
    /// </summary>
    private static void EnsureInstalled()
    {
        // GetInstanceDontCreateDefault, not Instance: the latter would fabricate a
        // settings object before the game has loaded its own.
        if (LocalizationSettings.GetInstanceDontCreateDefault() == null)
            return;

        var locales = LocalizationSettings.AvailableLocales?.Locales;
        if (locales == null || locales.Count == 0)
            return; // Addressables has not finished loading the locale list yet

        var providerOurs = LocalizationSettings.StringDatabase != null &&
                           LocalizationSettings.StringDatabase.TableProvider is ChineseTableProvider;

        if (!providerOurs)
            Install(locales);

        AssertSelectedLocale();
    }

    /// <summary>Wraps the string table provider and turns Chinese on if configured.</summary>
    private static void Install(List<Locale> locales)
    {
        _baseLocale = locales.Find(l => l != null && l.Identifier.Code == "en")
                     ?? locales.Find(l => l != null);
        if (_baseLocale == null)
            return;

        // Build the string table before swapping the provider in, so the first
        // lookup after the switch already has data. The English table is fetched
        // through the game's own provider (ours is not installed yet) and only
        // supplies the SharedTableData — the key ↔ id map the UI references.
        var table = BuildStringTable();
        LocalizationSettings.StringDatabase.TableProvider =
            new ChineseTableProvider(LocalizationSettings.StringDatabase.TableProvider, table);

        _log.LogInfo($"wrapped the string table provider ({table.Count} zh string entries)");

        var wanted = (StartLanguage.Value ?? "en").Trim().ToLowerInvariant();
        if (wanted != Code || _userChoseLanguage)
            return;

        if (IsActive)
        {
            // Chinese was already selected (the game's saved language asked for it
            // before we were installed), so nothing will re-read the string table
            // on its own. Re-selecting the locale makes every GameObjectLocalizer
            // apply its variants again — and because our provider keys off the
            // locale *code*, the intermediate step draws exactly the same thing.
            LocalizationSettings.SelectedLocale = _baseLocale;
        }

        EnableChinese();
    }

    private static StringTable BuildStringTable()
    {
        var table = ScriptableObject.CreateInstance<StringTable>();

        // Reuse the game's own SharedTableData: that is what maps key names to the
        // numeric ids the UI components reference, so every existing key resolves.
        var english = LocalizationSettings.StringDatabase.GetTable(StringTableCollection, _baseLocale);
        if (english != null && english.SharedData != null)
        {
            table.SharedData = english.SharedData;
            table.LocaleIdentifier = Code;

            foreach (var key in Strings.Keys)
                table.AddEntry(key, Strings[key]);
        }
        else
        {
            _log.LogWarning("could not reach the English StringTable; building an empty one");
            table.LocaleIdentifier = Code;
        }

        return table;
    }

    /// <summary>
    /// Selects the Chinese locale (English code, translated strings).
    ///
    /// The locale object differs from the game's English locale by name, so
    /// <c>LocalizationSettings.SetSelectedLocale</c> treats it as a real change
    /// and every <see cref="GameObjectLocalizer"/> re-applies its variants.
    /// </summary>
    private static void EnableChinese()
    {
        if (_baseLocale == null)
        {
            var locales = LocalizationSettings.AvailableLocales?.Locales;
            _baseLocale = locales?.Find(l => l != null && l.Identifier.Code == "en")
                          ?? locales?.Find(l => l != null);
            if (_baseLocale == null)
                return;
        }

        if (_locale == null)
        {
            _locale = UnityEngine.Object.Instantiate(_baseLocale);
            _locale.name = "中文";
            // English code on purpose: asset lookups then use the English asset
            // table through the game's own code path.
            _locale.Identifier = "en";
            _locale.LocaleName = LocaleName;
            _locale.SortOrder = 10001;
        }

        if (!_chineseMode)
            _log.LogInfo("switched to Chinese");

        _chineseMode = true;
        LocalizationSettings.SelectedLocale = _locale;
    }

    /// <summary>
    /// Back to the game's own English locale and English strings.
    ///
    /// <paramref name="reselect"/> is false when the caller is about to select a
    /// locale itself (the game's own SetLanguage does), so the switch does not
    /// flash through English on the way to Japanese.
    /// </summary>
    private static void DisableChinese(bool reselect = true)
    {
        _chineseMode = false;

        if (reselect && _baseLocale != null)
            LocalizationSettings.SelectedLocale = _baseLocale;

        _log.LogInfo("switched back to English");
    }

    /// <summary>
    /// Keeps the game on the English-code locale while Chinese is selected.
    ///
    /// Unity Localization resolves the selected locale lazily from its startup
    /// locale selectors, and the game re-reads its saved language when its
    /// MainManager starts. Either can put the game into Japanese (or English
    /// proper) with no user action involved — after a level transition, for
    /// example — which used to leave the player looking at an English or Japanese
    /// game. Note that a reset to the English locale is harmless by itself (the
    /// strings are still Chinese, because the provider keys off the code), so only
    /// a non-English locale is corrected here.
    /// </summary>
    private static void AssertSelectedLocale()
    {
        if (!_chineseMode || _userChoseLanguage)
            return;

        var selected = LocalizationSettings.SelectedLocale;
        if (selected == null || selected.Identifier.Code != "en")
            EnableChinese();
    }

    // ------------------------------------------------------------- enum names

    /// <summary>
    /// The game's hard-coded name table. English is derived from the enum name,
    /// Japanese comes from a switch — Chinese needs its own branch.
    /// </summary>
    [HarmonyPatch(typeof(EnumToString))]
    private static class EnumToStringPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnumToString.String))]
        private static bool Prefix(Enum value, ref string __result)
        {
            if (!IsActive)
                return true;

            var key = value.GetType().Name + "." + value.ToString();
            if (EnumNames.TryGetValue(key, out var translated))
            {
                __result = translated;
                return false;
            }

            return true; // fall back to the game's own English formatting
        }
    }

    /// <summary>
    /// The level-select tooltip. <c>LevelButton.OnPointerEnter</c> hard-codes
    /// "Completed!" / "Symmetrical!!" and only swaps them for Japanese inside an
    /// <c>if (locale == "ja")</c> branch, so Chinese kept the English text.
    /// </summary>
    [HarmonyPatch(typeof(LevelButton))]
    private static class LevelButtonTooltipPatch
    {
        // Mirrors LevelButton.OnPointerEnter's Japanese wording: クリア済! / ミラー!!
        private const string CompletedText = "已完成！";
        private const string SymmetricalText = "镜像！！";

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LevelButton.OnPointerEnter))]
        private static bool Prefix(LevelButton __instance, PointerEventData eventData)
        {
            if (!IsActive)
                return true;

            if (__instance.locked ||
                (MainManager.instance != null && MainManager.instance.transiting))
                return false;

            // The original sets this; replicate it so the button still highlights.
            __instance.hovering = true;

            if (__instance.audioSource != null && __instance.mouseOverSound != null)
            {
                __instance.audioSource.clip = __instance.mouseOverSound;
                __instance.audioSource.Play();
            }

            if (TooltipSystem.instance == null || __instance.dontShowTooltip)
                return false;

            var props = __instance.completed ? CompletedText : "";
            var summary = __instance.symmetrical ? SymmetricalText : "";
            TooltipSystem.instance.Show(__instance.level.displayName, props, summary);
            return false;
        }
    }

    /// <summary>
    /// The settings screen's language buttons call <c>SubManager.SetLanguage</c>
    /// with a hard-coded code, and the scene's option list is literally
    /// <c>["en", "ja"]</c> — there is no third entry to click and no way to add
    /// one without Unity Editor access to the scene.
    ///
    /// While Chinese is selected the game's locale is the English one, so asking
    /// for English here means "turn Chinese off" rather than "switch language":
    /// the button is what makes Chinese reachable from the game's own settings.
    /// </summary>
    [HarmonyPatch(typeof(SubManager))]
    private static class SetLanguageCyclePatch
    {
        private static readonly string[] Cycle = { "en", "ja", Code };

        [HarmonyPrefix]
        [HarmonyPatch(nameof(SubManager.SetLanguage))]
        private static bool Prefix(string language)
        {
            var current = LocalizationSettings.SelectedLocale?.Identifier.Code ?? "en";
            var requested = (language ?? "").Trim().ToLowerInvariant();

            // The player just picked a language from the settings screen; from
            // here on the mod stops re-asserting its own choice.
            _userChoseLanguage = true;

            if (requested == Code)
            {
                EnableChinese();
                return false;
            }

            // Chinese rides on the English locale, so "English" while Chinese is
            // showing is the way back out of it.
            if (IsActive && requested == "en")
            {
                DisableChinese();
                return false;
            }

            if (requested != current)
                return true; // a genuine change, let the game handle it

            var next = Cycle[(Array.IndexOf(Cycle, current) + 1) % Cycle.Length];
            _log?.LogInfo($"language control asked for {requested} while already active; " +
                          $"cycling to {next}");

            if (next == Code)
                EnableChinese();
            else
                MainManager.instance?.SetLanguage(next);
            return false;
        }
    }

    /// <summary>English -> Japanese -> Chinese -> English ...</summary>
    private static void CycleLanguage()
    {
        _userChoseLanguage = true;

        var current = IsActive ? Code : LocalizationSettings.SelectedLocale?.Identifier.Code;
        var next = current == "en" ? "ja" : current == "ja" ? Code : "en";

        if (next == Code)
        {
            EnableChinese();
            return;
        }

        // Leaving Chinese: the flag has to go before the locale changes, so the
        // localizers that re-apply for English/Japanese stop getting the
        // translated table.
        _chineseMode = false;

        var locale = LocalizationSettings.AvailableLocales?.GetLocale(next);
        if (locale != null)
            LocalizationSettings.SelectedLocale = locale;
    }

    /// <summary>
    /// The game's own language selector only knows "en" and "ja" and indexes the
    /// locale list by position, so it would silently fall back to English for
    /// anything else. Resolve by code instead.
    /// </summary>
    [HarmonyPatch(typeof(MainManager))]
    private static class SetLanguagePatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MainManager.SetLanguage))]
        private static bool Prefix(string language, MainManager __instance)
        {
            if (string.IsNullOrEmpty(language))
                return true;

            var requested = language.Trim().ToLowerInvariant();

            // A real English or Japanese choice: the Chinese strings must stop
            // being served for the English locale.
            if (requested == "en" || requested == "ja")
            {
                if (IsActive)
                    DisableChinese(reselect: false); // the game picks the locale next
                return true;
            }

            // "zh" only ever comes from this mod's own cycle.
            if (requested == Code)
            {
                EnableChinese();
                return false;
            }

            var locale = LocalizationSettings.AvailableLocales?.GetLocale(language);
            if (locale == null)
                return true;

            LocalizationSettings.SelectedLocale = locale;
            Debug.Log("Set language to " + LocalizationSettings.SelectedLocale);
            return false;
        }
    }

    // ---------------------------------------------------------------- provider

    /// <summary>
    /// Serves the Chinese string table while Chinese is selected, and lets the
    /// game handle everything else.
    ///
    /// The game reads its UI text through <c>GameObjectLocalizer</c> property
    /// variants; the <c>LocalizedString</c> inside the variant is resolved for the
    /// *selected* locale, which is our Chinese locale (English code). Requests for
    /// the string table therefore arrive here with an English-coded locale and get
    /// the translated table; asset requests are never intercepted at all, so every
    /// sprite, font and material is resolved by the game itself.
    ///
    /// An *invalid* handle is the documented way to say "not mine":
    /// <c>LoadTableOperation.TryLoadWithTableProvider</c> only accepts the
    /// provider's result when the handle is valid and otherwise runs the game's
    /// own Addressables lookup.
    /// </summary>
    private sealed class ChineseTableProvider : ITableProvider
    {
        private readonly ITableProvider _inner;
        private readonly StringTable _table;

        public ChineseTableProvider(ITableProvider inner, StringTable table)
        {
            _inner = inner;
            _table = table;
        }

        public AsyncOperationHandle<TTable> ProvideTableAsync<TTable>(string tableCollectionName, Locale locale)
            where TTable : LocalizationTable
        {
            if (ChineseLocalization.IsActive &&
                tableCollectionName == StringTableCollection &&
                _table != null &&
                typeof(StringTable).IsAssignableFrom(typeof(TTable)) &&
                IsServed(locale))
            {
                AsyncOperationHandle completed =
                    Addressables.ResourceManager.CreateCompletedOperation(_table, null);
                return completed.Convert<TTable>();
            }

            if (_inner != null)
            {
                try
                {
                    var handle = _inner.ProvideTableAsync<TTable>(tableCollectionName, locale);
                    if (handle.IsValid())
                        return handle;
                }
                catch (Exception error)
                {
                    _log?.LogWarning("inner table provider failed for " +
                                     $"{tableCollectionName}: {error.Message}");
                }
            }

            return default;
        }

        /// <summary>
        /// Chinese rides on the English locale code, so both our own locale and
        /// the game's English one are served the translated table while Chinese is
        /// selected. A Japanese (or any other) request is left to the game.
        /// </summary>
        private static bool IsServed(Locale locale) =>
            locale == null || locale.Identifier.Code == "en";
    }

    // ------------------------------------------------------------ json shapes

    #pragma warning disable 0649 // assigned by JsonUtility via reflection
    [Serializable]
    private class StringTableFile
    {
        public string locale;
        public StringEntry[] strings;
    }

    [Serializable]
    private class StringEntry
    {
        public string key;
        public string value;
    }

    [Serializable]
    private class EnumNameFile
    {
        public string locale;
        public EnumNameEntry[] names;
    }

    [Serializable]
    private class EnumNameEntry
    {
        public string @enum;
        public string name;
        public string value;
    }
    #pragma warning restore 0649
}
