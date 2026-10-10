using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HvrChinese;

/// <summary>
/// Replaces sprite artwork that has English baked into it.
///
/// The game's string table covers text drawn with TextMeshPro, but the buttons are
/// images with the word already rendered into them ("Story", "Collection",
/// "Settings", …). Those cannot be translated by the string table, so
/// hand-translated PNGs ship with the plugin and are swapped in at run time.
///
/// The important subtlety is *where* the sprite has to be swapped. Buttons do not
/// keep their sprite on the Image: <c>CustomButton.Update</c> assigns
/// <c>image.sprite = frameIdle / frameHover / frameSelected</c> **every frame**,
/// and <c>Card</c>, <c>CardSingleUse</c> and <c>CheckLevelSwitch</c> do the same
/// with their own fields. Replacing <see cref="Image.sprite"/> therefore only
/// lasts until the next frame — which is why an override can log "replaced" and
/// still have no visible effect.
///
/// So the serialized <see cref="Sprite"/> fields on the loaded components are
/// rewritten instead, and the Image is only touched for artwork that nothing else
/// overwrites. Both are undone when Chinese is switched off, so English and
/// Japanese keep their own artwork.
/// </summary>
internal static class SpriteOverride
{
    private const string ResourcePrefix = "HvrChinese.sprites.";

    private static readonly Regex UnsafeChars = new Regex("[^A-Za-z0-9._-]+");

    private static readonly Dictionary<string, byte[]> Overrides = new Dictionary<string, byte[]>();

    /// <summary>Our translated sprite per key, built once from the first original.</summary>
    private static readonly Dictionary<string, Sprite> Replacements = new Dictionary<string, Sprite>();

    /// <summary>Sprite-typed fields per component type, reflected once.</summary>
    private static readonly Dictionary<Type, FieldInfo[]> SpriteFields = new Dictionary<Type, FieldInfo[]>();

    /// <summary>What a component field held before it was replaced.</summary>
    private static readonly Dictionary<object, Dictionary<FieldInfo, Sprite>> OriginalFields =
        new Dictionary<object, Dictionary<FieldInfo, Sprite>>();

    /// <summary>
    /// Images we replaced, with the English sprite they held. Something in the
    /// game (the property-variant localizer re-applying the asset table) writes
    /// the original artwork back onto the Image after a sweep, so these are
    /// re-asserted every frame.
    /// </summary>
    private sealed class Watched
    {
        public string Key;
        public Sprite Replacement;
    }

    private static readonly Dictionary<Image, Watched> WatchedImages = new Dictionary<Image, Watched>();

    /// <summary>
    /// Serialized sprite fields we replaced. The scenes' GameObjectLocalizers track
    /// these very fields — the button art is stored as
    /// <c>LocalizedAssetProperty</c> variants on `frameIdle` / `frameHover` /
    /// `frameSelected` — and rewrite them from the asset table for the selected
    /// locale, which is the English artwork. They are re-asserted every frame for
    /// the same reason the images are.
    /// </summary>
    private sealed class WatchedField
    {
        public object Component;
        public FieldInfo Field;
        public string Key;
        public Sprite Replacement;
    }

    private static readonly List<WatchedField> WatchedFields = new List<WatchedField>();

    private static ManualLogSource _log;
    private static bool _loaded;
    private static bool _applied;
    private static float _nextRefresh;

    public static void Initialise(ManualLogSource log) => _log = log;

    /// <summary>Number of translated sprites embedded in the plugin.</summary>
    public static int Count
    {
        get
        {
            Load();
            return Overrides.Count;
        }
    }

    public static void Tick()
    {
        Load();

        if (Overrides.Count == 0)
            return;

        var wantChinese = ChineseLocalization.IsActive;
        if (wantChinese != _applied)
        {
            _applied = wantChinese;
            if (wantChinese)
                Apply();
            else
                Restore();
            _nextRefresh = Time.unscaledTime + 2f;
            return;
        }

        if (!wantChinese)
            return;

        // The property-variant localizer tracks the buttons' own sprite fields
        // (frameIdle / frameHover / frameSelected) as well as their Images, and
        // rewrites them from the asset table for the selected locale — the
        // English artwork. Put ours back the moment that happens, otherwise a
        // button sits on English art until something else touches it.
        ReassertFields();
        ReassertImages();

        // Images come and go with scenes, so keep sweeping until every one has
        // been seen.
        if (Time.unscaledTime < _nextRefresh)
            return;
        _nextRefresh = Time.unscaledTime + 2f;

        Apply();
    }

    /// <summary>
    /// Undoes the localizer writing the untranslated sprite back into a button's
    /// serialized field. Only touches fields whose current value is the
    /// untranslated sprite for a key we replaced, so a sprite the game set on
    /// purpose is left alone.
    /// </summary>
    private static void ReassertFields()
    {
        if (WatchedFields.Count == 0)
            return;

        var drifted = 0;
        for (var i = WatchedFields.Count - 1; i >= 0; i--)
        {
            var watched = WatchedFields[i];
            if (watched.Component == null)
            {
                WatchedFields.RemoveAt(i);
                continue;
            }

            if (!(watched.Field.GetValue(watched.Component) is Sprite current) || current == null)
                continue;
            if (current == watched.Replacement)
                continue;
            if (SafeName(current.name) != watched.Key)
                continue;

            watched.Field.SetValue(watched.Component, watched.Replacement);
            drifted++;
        }

        LogReversions(drifted);
    }

    /// <summary>
    /// Undoes an overwrite of a translated sprite. Only touches images whose
    /// current sprite is the *untranslated* one for a key we replaced, so a
    /// sprite the game set on purpose (a lock icon, a different state) is left
    /// alone.
    /// </summary>
    private static float _verifyAt;

    /// <summary>
    /// Reports what the buttons are *actually showing* a few seconds after a
    /// sweep, so a sprite that is replaced but not displayed can be told apart
    /// from one that was never replaced.
    /// </summary>
    private static void Verify()
    {
        if (_verifyAt <= 0f || Time.unscaledTime < _verifyAt)
            return;
        _verifyAt = 0f;

        foreach (var watched in WatchedFields)
        {
            if (watched.Component == null)
                continue;
            var value = watched.Field.GetValue(watched.Component) as Sprite;
            _log.LogInfo($"  field check: {watched.Field.Name} = " +
                         $"'{value?.name ?? "null"}' — " +
                         (value == watched.Replacement ? "translated" : "NOT the translated sprite"));
        }

        foreach (var pair in WatchedImages)
        {
            var image = pair.Key;
            if (image == null)
                continue;

            var shown = image.sprite;
            var ok = shown != null && shown == pair.Value.Replacement;
            _log.LogInfo($"  display check: {image.gameObject.name} shows " +
                         $"'{shown?.name ?? "null"}' — " +
                         (ok ? "translated" : "NOT the translated sprite"));
        }
    }

    private static void ReassertImages()
    {
        if (WatchedImages.Count == 0)
            return;

        var drifted = 0;
        foreach (var pair in WatchedImages)
        {
            var image = pair.Key;
            if (image == null)
                continue;

            var watched = pair.Value;
            var current = image.sprite;
            if (current == null || current == watched.Replacement)
                continue;
            if (SafeName(current.name) != watched.Key)
                continue;

            image.sprite = watched.Replacement;
            drifted++;
        }

        LogReversions(drifted);
    }

    private static float _nextRevertLog;

    private static void LogReversions(int drifted)
    {
        if (drifted <= 0 || Time.unscaledTime < _nextRevertLog)
            return;

        _nextRevertLog = Time.unscaledTime + 5f;
        _log.LogInfo($"re-applied {drifted} translated sprite(s) that had been " +
                     "reverted to the original artwork");
    }

    /// <summary>How many references each override key replaced on the last sweep.</summary>
    private static readonly Dictionary<string, int> Applied = new Dictionary<string, int>();

    private static void Apply()
    {
        var replaced = 0;
        Applied.Clear();

        // 1. Serialized Sprite fields. This is what the buttons actually draw
        //    from, and the only swap that survives their per-frame Update.
        foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (component == null)
                continue;

            try
            {
                foreach (var field in SpriteFieldsOf(component.GetType()))
                {
                    if (!(field.GetValue(component) is Sprite current) || current == null)
                        continue;

                    var key = SafeName(current.name);
                    if (!Overrides.TryGetValue(key, out var png))
                        continue;

                    var replacement = ReplacementFor(key, png, current);
                    if (replacement == null || replacement == current)
                        continue;

                    Remember(component, field, current);
                    field.SetValue(component, replacement);
                    if (!WatchedFields.Exists(w => ReferenceEquals(w.Component, component) && w.Field == field))
                        WatchedFields.Add(new WatchedField
                        {
                            Component = component,
                            Field = field,
                            Key = key,
                            Replacement = replacement
                        });
                    Applied[key] = Applied.GetValueOrDefault(key) + 1;
                    replaced++;
                }
            }
            catch (Exception error)
            {
                // One odd component must not abort the whole sweep.
                _log.LogWarning($"could not patch sprites on {component.GetType().Name}: " +
                                error.Message);
            }
        }

        // 2. Images whose sprite nothing else overwrites — artwork driven purely
        //    by the localization variants.
        foreach (var image in Resources.FindObjectsOfTypeAll<Image>())
        {
            if (image == null || image.sprite == null)
                continue;

            var key = SafeName(image.sprite.name);
            if (!Overrides.TryGetValue(key, out var png))
                continue;
            if (image.sprite.texture != null && image.sprite.texture.name == "override:" + key)
                continue; // already replaced

            var replacement = ReplacementFor(key, png, image.sprite);
            if (replacement == null || replacement == image.sprite)
                continue;

            if (!WatchedImages.ContainsKey(image))
                WatchedImages[image] = new Watched { Key = key, Replacement = replacement };
            image.sprite = replacement;
            Applied[key] = Applied.GetValueOrDefault(key) + 1;
            replaced++;
        }

        if (replaced > 0)
            _verifyAt = Time.unscaledTime + 3f;

        Verify();

        if (replaced > 0 || _nextRefresh <= 0f)
        {
            // Per key, so it is obvious which sprite went where — and which
            // translated sprite nothing in the loaded scenes is using.
            var detail = string.Join(", ",
                Overrides.Keys.Select(k => $"{k}={(Applied.TryGetValue(k, out var n) ? n.ToString() : "0")}"));
            _log.LogInfo($"translated sprites applied: {replaced} reference(s) [{detail}]");

            foreach (var key in Overrides.Keys)
            {
                if (!Applied.ContainsKey(key))
                    _log.LogInfo($"  note: {key} is embedded but no loaded scene uses " +
                                 "that sprite name (yet)");
            }
        }
    }

    private static void Restore()
    {
        foreach (var pair in OriginalFields)
        {
            if (pair.Key == null)
                continue;
            foreach (var entry in pair.Value)
                entry.Key.SetValue(pair.Key, entry.Value);
        }
        OriginalFields.Clear();

        // The images themselves are left alone: leaving Chinese always goes with a
        // locale change, and the localizer re-applies the artwork for whichever
        // locale is selected then.
        WatchedImages.Clear();
        WatchedFields.Clear();
    }

    /// <summary>
    /// The translated sprite for a key, built once from the original it replaces
    /// so the pivot, pixels-per-unit and 9-slice border carry over. Failures are
    /// cached as null, so a PNG Unity cannot decode is reported once instead of
    /// on every sweep.
    /// </summary>
    private static Sprite ReplacementFor(string key, byte[] png, Sprite original)
    {
        if (Replacements.TryGetValue(key, out var cached))
            return cached;

        var texture = new Texture2D(2, 2);
        if (!texture.LoadImage(png))
        {
            // Interlaced, 16-bit or CMYK PNGs land here; without this the button
            // would silently stay English with nothing in the log to explain it.
            _log.LogWarning($"could not decode the translated sprite {key}.png — " +
                            "run tools/prepare_sprites.py on assets/sprites/");
            Replacements[key] = null;
            return null;
        }

        texture.name = "override:" + key;

        var replacement = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            original.pivot,
            original.pixelsPerUnit > 0f ? original.pixelsPerUnit : 100f,
            0u,
            SpriteMeshType.FullRect,
            original.border);
        replacement.name = original.name;

        Replacements[key] = replacement;
        return replacement;
    }

    private static FieldInfo[] SpriteFieldsOf(Type type)
    {
        if (!SpriteFields.TryGetValue(type, out var fields))
        {
            fields = type
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(Sprite))
                .ToArray();
            SpriteFields[type] = fields;
        }
        return fields;
    }

    private static void Remember(object component, FieldInfo field, Sprite original)
    {
        if (!OriginalFields.TryGetValue(component, out var saved))
        {
            saved = new Dictionary<FieldInfo, Sprite>();
            OriginalFields[component] = saved;
        }

        // Only the first value seen is the game's own; re-running after the game
        // reassigned the field must not store our translation as the "original".
        if (!saved.ContainsKey(field))
            saved[field] = original;
    }

    /// <summary>
    /// Puts the translated sprite on an Image right now.
    ///
    /// The buttons assign <c>image.sprite</c> from a serialized field every frame,
    /// and Unity's <c>Image.sprite</c> setter only marks the graphic dirty when the
    /// value *changes*. A button whose field was swapped while its Image already
    /// showed the untranslated sprite therefore keeps drawing the old art until
    /// something changes the sprite — which is what hovering does, and why a
    /// translated button used to look English until it was hovered. Forcing the
    /// assignment from a postfix on the button's own Update makes the swap visible
    /// on the spot and survives the localizer rewriting the fields.
    /// </summary>
    public static void ForceImage(Image image)
    {
        if (image == null || image.sprite == null)
            return;

        var key = SafeName(image.sprite.name);
        if (!Overrides.TryGetValue(key, out var png))
            return;

        var replacement = ReplacementFor(key, png, image.sprite);
        if (replacement == null || replacement == image.sprite)
            return;

        image.sprite = replacement;
    }

    /// <summary>
    /// Mirrors <c>tools/extract_sprites.py</c>'s <c>safe_name</c>, so the embedded
    /// resource name can be derived from a sprite name. Cached because the button
    /// postfixes call it every frame.
    /// </summary>
    private static readonly Dictionary<string, string> SafeNameCache = new Dictionary<string, string>();

    private static string SafeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "unnamed";

        if (SafeNameCache.TryGetValue(name, out var cached))
            return cached;

        var safe = UnsafeChars.Replace(name, "_").Trim('_');
        if (string.IsNullOrEmpty(safe))
            safe = "unnamed";

        SafeNameCache[name] = safe;
        return safe;
    }

    private static void Load()
    {
        if (_loaded)
            return;
        _loaded = true;

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;

            var key = resource.Substring(ResourcePrefix.Length,
                resource.Length - ResourcePrefix.Length - ".png".Length);

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream == null)
                continue;

            using var reader = new MemoryStream();
            stream.CopyTo(reader);
            Overrides[key] = reader.ToArray();
        }

        _log.LogInfo($"loaded {Overrides.Count} translated sprite(s): " +
                     string.Join(", ", Overrides.Keys));
    }

    // ------------------------------------------------------------- button hooks

    /// <summary>
    /// Forces the translated sprite onto a button's Image right after the game
    /// assigns it, so a swapped field is visible immediately instead of waiting
    /// for something else to change the sprite.
    /// </summary>
    [HarmonyPatch(typeof(CustomButton))]
    private static class CustomButtonUpdatePatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        private static void Postfix(CustomButton __instance) => ForceImage(__instance.image);
    }

    [HarmonyPatch(typeof(LevelButton))]
    private static class LevelButtonUpdatePatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        private static void Postfix(LevelButton __instance) => ForceImage(__instance.image);
    }

    [HarmonyPatch(typeof(Card))]
    private static class CardUpdatePatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        private static void Postfix(Card __instance) => ForceImage(__instance.cardFrame);
    }

    [HarmonyPatch(typeof(CardSingleUse))]
    private static class CardSingleUseUpdatePatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        private static void Postfix(CardSingleUse __instance) => ForceImage(__instance.cardFrame);
    }
}
