using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Il2CppGasSim;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace CombustorGasMix;

/// <summary>
/// Lets any injector already placed in the game feed more than one gas.
/// The game still owns the injector: this mod only splits its rate between
/// temporary generator copies during GasGrid.RunGenerators, then removes them.
/// </summary>
public sealed partial class GasMixMod : MelonMod
{
    private const int MaxGases = 5;
    private const float MinShare = 0.01f;
    private const float MaxExtraShare = 0.95f;

    private sealed class Extra
    {
        internal int Species;
        internal float Share; // fraction of the injector's full rate
    }

    private sealed class Mix
    {
        internal int X;
        internal int Y;
        internal int BaseSpecies;
        internal readonly List<Extra> Extras = new List<Extra>();

        internal float Total
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < Extras.Count; i++)
                {
                    total += Extras[i].Share;
                }
                return total;
            }
        }
    }

    private struct RateState
    {
        internal Generator Generator;
        internal float Rate;
    }

    private sealed class GhostSet
    {
        internal readonly List<Generator> Items = new List<Generator>();
    }

    private sealed class PendingMix
    {
        internal int X;
        internal int Y;
        internal int BaseSpecies;
        internal readonly List<Extra> Extras = new List<Extra>();
        internal float Expires;
    }

    private static readonly HarmonyLib.Harmony Hooks = new HarmonyLib.Harmony("combustor.gas-mix");
    private static readonly Dictionary<IntPtr, Mix> Mixes = new Dictionary<IntPtr, Mix>();
    private static readonly Dictionary<IntPtr, GhostSet> Ghosts = new Dictionary<IntPtr, GhostSet>();
    private static readonly List<RateState> SavedRates = new List<RateState>();
    private static readonly HashSet<IntPtr> ActiveGhostPointers = new HashSet<IntPtr>();
    private static readonly List<PendingMix> Pending = new List<PendingMix>();

    private static GasGrid activeGrid;
    private static bool injecting;
    private static bool enabled;
    private static bool panelErrorLogged;

    private static IntPtr openKey = IntPtr.Zero;
    private static IntPtr lastInjector = IntPtr.Zero;
    private static int chooserSlot = -1;
    private static Vector2 chooserScroll;
    private static string cachedLanguage;
    private static float languageAt;
    private static Rect mixPanelRect;
    private static Vector2 guiMouse;
    private static bool mixPanelRectValid;
    private static float chooserOpenedAt;

    public override void OnInitializeMelon()
    {
        try
        {
            Patch(typeof(GasGrid), "RunGenerators", "BeforeInjection", "AfterInjection", "FinalizeInjection");
            Patch(typeof(GasSimHud), "DrawInjectorPanel", null, "InjectorPanel");
            Patch(typeof(GasSimInput), "Update", "BeforeInput", null);
            Patch(typeof(GasSimulation), "SaveTo", null, "Saved");
            Patch(typeof(GasSimulation), "LoadFrom", null, "Loaded");
            Patch(typeof(GasGrid), "Clear", null, "Cleared");
            Patch(typeof(GasGrid), "Dispose", null, "Disposed");
            enabled = true;
#if GASMIX_SELFTEST
            Application.runInBackground = true;
#endif
            LoggerInstance.Msg("Gas Mix ready. Click an injector and open Gas mix.");
        }
        catch (Exception ex)
        {
            Hooks.UnpatchSelf();
            LoggerInstance.Error("Gas Mix could not start:\n" + ex);
        }
    }

    public override void OnDeinitializeMelon()
    {
        enabled = false;
        try
        {
            if (injecting)
            {
                RestoreInjection();
            }
            Hooks.UnpatchSelf();
        }
        catch
        {
        }
    }

    private static void Patch(Type type, string target, string prefix, string postfix, string finalizer = null)
    {
        MethodInfo method = AccessTools.DeclaredMethod(type, target, (Type[])null, (Type[])null)
            ?? throw new MissingMethodException(type.FullName, target);
        Hooks.Patch((MethodBase)method, Hook(prefix), Hook(postfix), (HarmonyMethod)null, Hook(finalizer), (HarmonyMethod)null);

        static HarmonyMethod Hook(string name)
        {
            if (name == null)
            {
                return null;
            }
            MethodInfo hook = typeof(GasMixMod).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (hook == null)
            {
                throw new MissingMethodException(typeof(GasMixMod).FullName, name);
            }
            return new HarmonyMethod(hook);
        }
    }

    private static IntPtr Key(Generator generator)
    {
        return generator == null ? IntPtr.Zero : ((Il2CppObjectBase)generator).Pointer;
    }

    private static Generator Find(Il2CppSystem.Collections.Generic.List<Generator> list, int count, IntPtr key)
    {
        for (int i = 0; i < count; i++)
        {
            Generator candidate = list[i];
            if (candidate != null && Key(candidate) == key)
            {
                return candidate;
            }
        }
        return null;
    }

    private static Generator FindAt(Il2CppSystem.Collections.Generic.List<Generator> list, int x, int y, int species)
    {
        if (list == null)
        {
            return null;
        }
        for (int i = 0; i < list.Count; i++)
        {
            Generator candidate = list[i];
            if (candidate != null && candidate.x == x && candidate.y == y && candidate.species == species)
            {
                return candidate;
            }
        }
        return null;
    }

    private static void Normalize(Mix mix)
    {
        if (mix == null)
        {
            return;
        }
        for (int i = mix.Extras.Count - 1; i >= 0; i--)
        {
            Extra extra = mix.Extras[i];
            if (extra == null || extra.Species == mix.BaseSpecies || extra.Species < 0)
            {
                mix.Extras.RemoveAt(i);
            }
            else
            {
                if (!float.IsFinite(extra.Share))
                {
                    extra.Share = MinShare;
                }
                extra.Share = Mathf.Clamp(extra.Share, MinShare, MaxExtraShare);
            }
        }
        float total = 0f;
        for (int i = 0; i < mix.Extras.Count; i++)
        {
            total += mix.Extras[i].Share;
        }
        if (!float.IsFinite(total))
        {
            for (int i = 0; i < mix.Extras.Count; i++)
            {
                mix.Extras[i].Share = MinShare;
            }
            total = mix.Extras.Count * MinShare;
        }
        if (total > MaxExtraShare)
        {
            float scale = MaxExtraShare / total;
            for (int i = 0; i < mix.Extras.Count; i++)
            {
                mix.Extras[i].Share *= scale;
            }
        }
    }

    private static GhostSet GetGhostSet(IntPtr key)
    {
        if (!Ghosts.TryGetValue(key, out GhostSet set))
        {
            set = new GhostSet();
            Ghosts[key] = set;
        }
        return set;
    }

    private static void EnsureGhosts(GhostSet set, int count)
    {
        while (set.Items.Count < count)
        {
            set.Items.Add(new Generator());
        }
        while (set.Items.Count > count)
        {
            set.Items.RemoveAt(set.Items.Count - 1);
        }
    }

    private static void PruneMixes(Il2CppSystem.Collections.Generic.List<Generator> list, int count)
    {
        if (Mixes.Count == 0)
        {
            return;
        }
        List<IntPtr> keys = new List<IntPtr>(Mixes.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            IntPtr key = keys[i];
            Mix mix = Mixes[key];
            Generator generator = Find(list, count, key);
            if (generator == null || mix.X != generator.x || mix.Y != generator.y)
            {
                Mixes.Remove(key);
                Ghosts.Remove(key);
                continue;
            }
            if (mix.BaseSpecies != generator.species)
            {
                mix.BaseSpecies = generator.species;
                mix.Extras.RemoveAll((Extra extra) => extra.Species == generator.species);
                Normalize(mix);
            }
        }
    }

    private static void ApplyPending(GasGrid grid)
    {
        if (Pending.Count == 0)
        {
            return;
        }
        Il2CppSystem.Collections.Generic.List<Generator> list = grid.Generators;
        if (list == null)
        {
            return;
        }
        float now = Time.realtimeSinceStartup;
        for (int i = Pending.Count - 1; i >= 0; i--)
        {
            PendingMix pending = Pending[i];
            Generator generator = FindAt(list, pending.X, pending.Y, pending.BaseSpecies);
            if (generator != null)
            {
                Mix mix = new Mix
                {
                    X = pending.X,
                    Y = pending.Y,
                    BaseSpecies = pending.BaseSpecies
                };
                for (int e = 0; e < pending.Extras.Count; e++)
                {
                    Extra extra = pending.Extras[e];
                    mix.Extras.Add(new Extra { Species = extra.Species, Share = extra.Share });
                }
                Normalize(mix);
                Mixes[Key(generator)] = mix;
                Pending.RemoveAt(i);
            }
            else if (now > pending.Expires)
            {
                Pending.RemoveAt(i);
            }
        }
    }

    private static void PrepareInjection(GasGrid grid)
    {
        if (Mixes.Count == 0)
        {
            return;
        }
        Il2CppSystem.Collections.Generic.List<Generator> list = grid.Generators;
        if (list == null || list.Count == 0)
        {
            return;
        }

        int count = list.Count;
        PruneMixes(list, count);

        for (int i = 0; i < count; i++)
        {
            Generator generator = list[i];
            if (generator == null || !generator.enabled)
            {
                continue;
            }
            IntPtr key = Key(generator);
            if (!Mixes.TryGetValue(key, out Mix mix) || mix.Extras.Count == 0)
            {
                continue;
            }

            Normalize(mix);
            float total = mix.Total;
            if (total <= 0f)
            {
                continue;
            }

            float baseRate = generator.rate;
            float baseShare = Mathf.Clamp(1f - total, MinShare, 1f);
            SavedRates.Add(new RateState { Generator = generator, Rate = baseRate });
            generator.rate = baseRate * baseShare;

            GhostSet set = GetGhostSet(key);
            EnsureGhosts(set, mix.Extras.Count);
            for (int e = 0; e < mix.Extras.Count; e++)
            {
                Extra extra = mix.Extras[e];
                Generator ghost = set.Items[e];
                ghost.x = generator.x;
                ghost.y = generator.y;
                ghost.species = extra.Species;
                ghost.radius = generator.radius;
                ghost.rate = baseRate * extra.Share;
                ghost.temperature = generator.temperature;
                ghost.maxPressure = generator.maxPressure;
                ghost.enabled = true;
                ghost.scriptId = 0;
                list.Add(ghost);
                ActiveGhostPointers.Add(Key(ghost));
            }
        }
    }

    private static void RestoreInjection()
    {
        try
        {
            for (int i = 0; i < SavedRates.Count; i++)
            {
                RateState state = SavedRates[i];
                if (state.Generator != null)
                {
                    state.Generator.rate = state.Rate;
                }
            }

            if (activeGrid != null && ActiveGhostPointers.Count > 0)
            {
                Il2CppSystem.Collections.Generic.List<Generator> list = activeGrid.Generators;
                if (list != null)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        Generator item = list[i];
                        if (item != null && ActiveGhostPointers.Contains(Key(item)))
                        {
                            list.RemoveAt(i);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("Gas Mix cleanup: " + ex.Message);
        }
        finally
        {
            SavedRates.Clear();
            ActiveGhostPointers.Clear();
            activeGrid = null;
            injecting = false;
        }
    }

    private static void BeforeInjection(GasGrid __instance)
    {
        if (!enabled || injecting || __instance == null)
        {
            return;
        }
        injecting = true;
        activeGrid = __instance;
        try
        {
            ApplyPending(__instance);
            PrepareInjection(__instance);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("Gas Mix injection skipped: " + ex);
            RestoreInjection();
        }
    }

    private static void AfterInjection()
    {
        if (injecting)
        {
            RestoreInjection();
        }
    }

    private static void FinalizeInjection()
    {
        if (injecting)
        {
            RestoreInjection();
        }
    }

    private static void ClearAll()
    {
        if (injecting)
        {
            RestoreInjection();
        }
        Mixes.Clear();
        Ghosts.Clear();
        Pending.Clear();
        openKey = IntPtr.Zero;
        lastInjector = IntPtr.Zero;
        chooserSlot = -1;
    }

    private static void Cleared(GasGrid __instance, bool __0)
    {
        if (__0)
        {
            ClearAll();
        }
    }

    private static void Disposed(GasGrid __instance)
    {
        ClearAll();
    }

    private static string EnginePath(string directory, string name)
    {
        return Path.Combine(directory, GasSimulation.SanitizeName(name) + ".engine");
    }

    private static void Saved(GasSimulation __instance, string __0, string __1, bool __result)
    {
        if (!enabled || !__result || __instance == null)
        {
            return;
        }
        GasGrid grid = __instance.Grid;
        if (grid == null)
        {
            return;
        }
        try
        {
            string path = EnginePath(__0, __1) + ".gasmix";
            StringBuilder text = new StringBuilder();
            text.AppendLine("GASMIX 1");
            int written = 0;
            Il2CppSystem.Collections.Generic.List<Generator> list = grid.Generators;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Generator generator = list[i];
                    if (generator == null || !Mixes.TryGetValue(Key(generator), out Mix mix))
                    {
                        continue;
                    }
                    if (mix.X != generator.x || mix.Y != generator.y)
                    {
                        continue;
                    }
                    Normalize(mix);
                    if (mix.Extras.Count == 0)
                    {
                        continue;
                    }
                    text.Append(generator.x.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    text.Append(generator.y.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    text.Append(generator.species.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    text.Append(mix.Extras.Count.ToString(CultureInfo.InvariantCulture));
                    for (int e = 0; e < mix.Extras.Count; e++)
                    {
                        Extra extra = mix.Extras[e];
                        text.Append(' ').Append(extra.Species.ToString(CultureInfo.InvariantCulture));
                        text.Append(' ').Append(extra.Share.ToString("R", CultureInfo.InvariantCulture));
                    }
                    text.AppendLine();
                    written++;
                }
            }

            if (written == 0)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("Gas Mix could not be saved: " + ex.Message);
        }
    }

    private static void Loaded(GasSimulation __instance, string __0, string __1, bool __result)
    {
        if (!enabled)
        {
            return;
        }
        ClearAll();
        if (!__result || __instance == null)
        {
            return;
        }
        GasGrid grid = __instance.Grid;
        if (grid == null)
        {
            return;
        }
        try
        {
            string path = EnginePath(__0, __1) + ".gasmix";
#if GASMIX_SELFTEST
            MelonLogger.Msg("SELFTEST Loaded postfix result=" + __result + " path=" + path + " exists=" + File.Exists(path)
                + " gens=" + (grid.Generators != null ? grid.Generators.Count : -1));
#endif
            if (!File.Exists(path))
            {
                return;
            }
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || !lines[0].StartsWith("GASMIX", StringComparison.Ordinal))
            {
                return;
            }
            float expiry = Time.realtimeSinceStartup + 15f;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                {
                    continue;
                }
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int baseSpecies)
                    || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int extraCount)
                    || extraCount <= 0 || parts.Length < 4 + extraCount * 2)
                {
                    continue;
                }
                if (baseSpecies < 0 || baseSpecies >= grid.SpeciesCount)
                {
                    continue;
                }

                PendingMix pending = new PendingMix
                {
                    X = x,
                    Y = y,
                    BaseSpecies = baseSpecies,
                    Expires = expiry
                };
                bool valid = true;
                for (int e = 0; e < extraCount; e++)
                {
                    if (!int.TryParse(parts[4 + e * 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int species)
                        || !float.TryParse(parts[5 + e * 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float share))
                    {
                        valid = false;
                        break;
                    }
                    if (species < 0 || species >= grid.SpeciesCount || !float.IsFinite(share))
                    {
                        valid = false;
                        break;
                    }
                    pending.Extras.Add(new Extra { Species = species, Share = share });
                }
                if (!valid)
                {
                    continue;
                }

                Generator generator = FindAt(grid.Generators, x, y, baseSpecies);
#if GASMIX_SELFTEST
                MelonLogger.Msg("SELFTEST Loaded line x=" + x + " y=" + y + " base=" + baseSpecies
                    + " found=" + (generator != null) + " mixes=" + Mixes.Count);
#endif
                if (generator != null)
                {
                    Mix mix = new Mix { X = x, Y = y, BaseSpecies = baseSpecies };
                    for (int e = 0; e < pending.Extras.Count; e++)
                    {
                        Extra extra = pending.Extras[e];
                        mix.Extras.Add(new Extra { Species = extra.Species, Share = extra.Share });
                    }
                    Normalize(mix);
                    Mixes[Key(generator)] = mix;
                }
                else
                {
                    Pending.Add(pending);
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("Gas Mix could not be loaded: " + ex.Message);
        }
    }

    private static string Language()
    {
        float now = Time.realtimeSinceStartup;
        if (cachedLanguage == null || now >= languageAt)
        {
            languageAt = now + 1f;
            try
            {
                cachedLanguage = L.Language ?? "en";
            }
            catch
            {
                cachedLanguage = "en";
            }
        }
        return cachedLanguage;
    }

    private static bool Spanish()
    {
        return Language().StartsWith("es", StringComparison.OrdinalIgnoreCase);
    }

    private static string Tr(string english, string spanish)
    {
        return Spanish() ? spanish : english;
    }

    private static GUIStyle Style(GUIStyle style, GUIStyle fallback)
    {
        return style != null ? style : fallback;
    }

    private static string GasName(GasSimulation simulation, int index)
    {
        try
        {
            Il2CppReferenceArray<SpeciesDef> species = simulation.Species;
            if (species == null || index < 0 || index >= species.Length)
            {
                return "?";
            }
            SpeciesDef definition = species[index];
            if (definition == null)
            {
                return "?";
            }
            string name = definition.DisplayName;
            if (string.IsNullOrEmpty(name))
            {
                name = definition.name;
            }
            return string.IsNullOrEmpty(name) ? "?" : name;
        }
        catch
        {
            return "?";
        }
    }

    private static Color GasColor(GasSimulation simulation, int index)
    {
        try
        {
            Il2CppReferenceArray<SpeciesDef> species = simulation.Species;
            if (species == null || index < 0 || index >= species.Length)
            {
                return Color.white;
            }
            SpeciesDef definition = species[index];
            return definition == null ? Color.white : definition.color;
        }
        catch
        {
            return Color.white;
        }
    }

    private static bool SpeciesAllowed(GasSimInput input, int index)
    {
        if (input == null)
        {
            return true;
        }
        try
        {
            Il2CppSystem.Func<int, bool> gate = input.speciesGate;
            return gate == null || gate.Invoke(index);
        }
        catch
        {
            return true;
        }
    }

    private static bool BeforeInput()
    {
        if (!enabled)
        {
            return true;
        }
        if (chooserSlot != -1)
        {
            if (Time.realtimeSinceStartup > chooserOpenedAt + 60f)
            {
                chooserSlot = -1;
                return true;
            }
            return false;
        }
        if (mixPanelRectValid && mixPanelRect.Contains(guiMouse))
        {
            return false;
        }
        return true;
    }

    private static void TrackPanel(ref bool hasRect)
    {
        Rect last = GUILayoutUtility.GetLastRect();
        if (!hasRect)
        {
            mixPanelRect = last;
            hasRect = true;
        }
        else
        {
            float xMin = Mathf.Min(mixPanelRect.xMin, last.xMin);
            float yMin = Mathf.Min(mixPanelRect.yMin, last.yMin);
            float xMax = Mathf.Max(mixPanelRect.xMax, last.xMax);
            float yMax = Mathf.Max(mixPanelRect.yMax, last.yMax);
            mixPanelRect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }
    }

    private static void OpenChooser(int slot)
    {
        chooserSlot = slot;
        chooserScroll = Vector2.zero;
        chooserOpenedAt = Time.realtimeSinceStartup;
    }

    private static void InjectorPanel(GasSimHud __instance)
    {
        if (!enabled || __instance == null)
        {
            mixPanelRectValid = false;
            return;
        }
        try
        {
            DrawPanel(__instance);
            guiMouse = Event.current != null ? Event.current.mousePosition : Vector2.zero;
        }
        catch (Exception ex)
        {
            mixPanelRectValid = false;
            if (!panelErrorLogged)
            {
                panelErrorLogged = true;
                MelonLogger.Error("Gas Mix panel: " + ex);
            }
        }
    }

    private static void DrawPanel(GasSimHud hud)
    {
        Generator generator = hud._injectorEdited;
        GasSimulation simulation = hud._sim;
        if (generator == null || simulation == null)
        {
            chooserSlot = -1;
            mixPanelRectValid = false;
            return;
        }
        GasGrid grid = simulation.Grid;
        if (grid == null)
        {
            chooserSlot = -1;
            mixPanelRectValid = false;
            return;
        }

        GUIStyle button = Style(hud._button, GUI.skin.button);
        GUIStyle label = Style(hud._label, GUI.skin.label);
        GUIStyle dim = Style(hud._dim, GUI.skin.label);

        IntPtr key = Key(generator);
        if (lastInjector != key)
        {
            lastInjector = key;
            openKey = IntPtr.Zero;
            chooserSlot = -1;
        }

        if (chooserSlot != -1
            && Event.current != null
            && Event.current.type == EventType.KeyDown
            && Event.current.keyCode == KeyCode.Escape)
        {
            chooserSlot = -1;
            Event.current.Use();
        }

        bool tracked = false;
        Mixes.TryGetValue(key, out Mix mix);
        bool open = openKey == key;
        string title = (open ? "[-] " : "[+] ") + Tr("Gas mix", "Mezcla de gases");
        if (mix != null && mix.Extras.Count > 0)
        {
            title += " (" + (mix.Extras.Count + 1) + ")";
        }

        if (GUILayout.Button(title, button, GUILayout.Height(24f)))
        {
            open = !open;
            openKey = open ? key : IntPtr.Zero;
            if (!open)
            {
                chooserSlot = -1;
            }
        }
        TrackPanel(ref tracked);
        if (!open)
        {
            mixPanelRectValid = tracked;
            return;
        }

        if (mix == null)
        {
            GUILayout.Label(Tr("All gas comes from the gas chosen above.", "Todo el gas sale del gas elegido arriba."), dim);
            TrackPanel(ref tracked);
            if (GUILayout.Button(Tr("+ Add gas", "+ A\u00F1adir gas"), button, GUILayout.Height(22f)))
            {
                mix = new Mix { X = generator.x, Y = generator.y, BaseSpecies = generator.species };
                Mixes[key] = mix;
                OpenChooser(-2);
            }
            TrackPanel(ref tracked);
            if (mix != null && chooserSlot != -1)
            {
                DrawChooser(hud, simulation, generator, mix, button, label, dim, ref tracked);
            }
            mixPanelRectValid = tracked;
            return;
        }

        if (mix.X != generator.x || mix.Y != generator.y)
        {
            mix.X = generator.x;
            mix.Y = generator.y;
        }
        if (mix.BaseSpecies != generator.species)
        {
            mix.BaseSpecies = generator.species;
            mix.Extras.RemoveAll((Extra extra) => extra.Species == generator.species);
            Normalize(mix);
        }

        int primaryPercent = Mathf.RoundToInt((1f - mix.Total) * 100f);
        GUILayout.Label(
            string.Format(
                Tr("Main: <b>{0}</b>  {1}%", "Principal: <b>{0}</b>  {1}%"),
                GasName(simulation, generator.species),
                primaryPercent),
            label);
        TrackPanel(ref tracked);

        for (int i = 0; i < mix.Extras.Count; i++)
        {
            Extra extra = mix.Extras[i];
            GUILayout.BeginHorizontal();
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = GasColor(simulation, extra.Species);
            if (GUILayout.Button(GasName(simulation, extra.Species), button, GUILayout.Width(100f), GUILayout.Height(22f)))
            {
                OpenChooser(i);
            }
            GUI.backgroundColor = previous;

            float others = mix.Total - extra.Share;
            float max = Mathf.Max(MinShare, MaxExtraShare - others);
            float percent = extra.Share * 100f;
            float wanted = GUILayout.HorizontalSlider(percent, 1f, Mathf.Max(1f, max * 100f), GUILayout.Width(62f));
            if (Mathf.Abs(wanted - percent) > 0.01f)
            {
                extra.Share = Mathf.Clamp(wanted / 100f, MinShare, max);
            }
            GUILayout.Label(Mathf.RoundToInt(extra.Share * 100f) + "%", dim, GUILayout.Width(34f));
            if (GUILayout.Button("x", button, GUILayout.Width(20f), GUILayout.Height(22f)))
            {
                mix.Extras.RemoveAt(i);
                i--;
            }
            GUILayout.EndHorizontal();
            TrackPanel(ref tracked);
        }

        int totalPercent = Mathf.RoundToInt(mix.Total * 100f);
        GUILayout.Label(
            string.Format(
                Tr("Mix: {0}% extra + {1}% main", "Mezcla: {0}% extra + {1}% principal"),
                totalPercent,
                100 - totalPercent),
            dim);
        TrackPanel(ref tracked);

        GUILayout.BeginHorizontal();
        if (mix.Extras.Count < MaxGases - 1 && mix.Total < MaxExtraShare - 0.001f)
        {
            if (GUILayout.Button(Tr("+ Add gas", "+ A\u00F1adir gas"), button, GUILayout.Height(22f)))
            {
                OpenChooser(-2);
            }
        }
        if (GUILayout.Button(Tr("Clear mix", "Quitar mezcla"), button, GUILayout.Height(22f)))
        {
            Mixes.Remove(key);
            Ghosts.Remove(key);
            chooserSlot = -1;
            TrackPanel(ref tracked);
            mixPanelRectValid = tracked;
            return;
        }
        GUILayout.EndHorizontal();
        TrackPanel(ref tracked);

        if (chooserSlot != -1)
        {
            DrawChooser(hud, simulation, generator, mix, button, label, dim, ref tracked);
        }
        else
        {
            GUILayout.Label(
                Tr(
                    "Each extra gas takes a share of the injector's rate.",
                    "Cada gas extra recibe una parte del caudal del inyector."),
                dim);
            TrackPanel(ref tracked);
        }
        mixPanelRectValid = tracked;
    }

    private static void DrawChooser(
        GasSimHud hud,
        GasSimulation simulation,
        Generator generator,
        Mix mix,
        GUIStyle button,
        GUIStyle label,
        GUIStyle dim,
        ref bool tracked)
    {
        GUILayout.Label(Tr("Choose gas", "Elegir gas"), label);
        TrackPanel(ref tracked);
        Il2CppReferenceArray<SpeciesDef> species = simulation.Species;
        if (species == null)
        {
            chooserSlot = -1;
            return;
        }

        List<int> choices = new List<int>();
        for (int i = 0; i < species.Length; i++)
        {
            if (i == generator.species || !SpeciesAllowed(hud._input, i))
            {
                continue;
            }
            bool used = false;
            for (int e = 0; e < mix.Extras.Count; e++)
            {
                if (e != chooserSlot && mix.Extras[e].Species == i)
                {
                    used = true;
                    break;
                }
            }
            if (!used)
            {
                choices.Add(i);
            }
        }

        int picked = -1;
        chooserScroll = GUILayout.BeginScrollView(chooserScroll, GUILayout.Height(110f));
        for (int i = 0; i < choices.Count; i += 2)
        {
            GUILayout.BeginHorizontal();
            for (int column = 0; column < 2 && i + column < choices.Count; column++)
            {
                int index = choices[i + column];
                Color previous = GUI.backgroundColor;
                GUI.backgroundColor = GasColor(simulation, index);
                if (GUILayout.Button(GasName(simulation, index), button, GUILayout.Height(22f)))
                {
                    picked = index;
                }
                GUI.backgroundColor = previous;
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
        TrackPanel(ref tracked);

        if (picked >= 0)
        {
            if (chooserSlot == -2)
            {
                float room = MaxExtraShare - mix.Total;
                float share = Mathf.Clamp(room * 0.5f, MinShare, 0.25f);
                mix.Extras.Add(new Extra { Species = picked, Share = share });
            }
            else if (chooserSlot >= 0 && chooserSlot < mix.Extras.Count)
            {
                mix.Extras[chooserSlot].Species = picked;
            }
            Normalize(mix);
            chooserSlot = -1;
            return;
        }

        if (GUILayout.Button(Tr("Cancel", "Cancelar"), button, GUILayout.Height(22f)))
        {
            chooserSlot = -1;
        }
        TrackPanel(ref tracked);
    }
}

