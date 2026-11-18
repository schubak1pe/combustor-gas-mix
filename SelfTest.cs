#if GASMIX_SELFTEST
using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2CppGasSim;
using Il2CppInterop.Runtime;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace CombustorGasMix;

public sealed partial class GasMixMod
{
    private static int testPhase;
    private static float testAt;
    private static GasSimulation testSim;
    private static GasGrid testGrid;
    private static Generator testGenerator;
    private static int testBase;
    private static int testExtra;
    private static int testX;
    private static int testY;
    private static int testOriginalCount;

    public override void OnUpdate()
    {
        try
        {
            RunSelfTest();
        }
        catch (Exception ex)
        {
            MelonLogger.Error("SELFTEST crash: " + ex);
            testPhase = 99;
        }
    }

    private static T FindOne<T>() where T : UnityEngine.Object
    {
        try
        {
            var objects = UnityEngine.Object.FindObjectsByType(
                Il2CppType.Of<T>(),
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (objects == null || objects.Length == 0)
            {
                return UnityEngine.Object.FindObjectOfType<T>();
            }
            return objects[0].Cast<T>();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning("SELFTEST FindOne<" + typeof(T).Name + ">: " + ex.Message);
            return null;
        }
    }

    private static void RunSelfTest()
    {
        float now = Time.realtimeSinceStartup;

        if (testPhase == 0)
        {
            testPhase = 1;
            testAt = now + 2f;
            MainMenu menu = FindOne<MainMenu>();
            if (menu != null)
            {
                try
                {
                    MethodInfo start = AccessTools.Method(typeof(MainMenu), "StartSandbox");
                    start.Invoke(menu, null);
                    MelonLogger.Msg("SELFTEST StartSandbox invoked.");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("SELFTEST StartSandbox failed: " + ex);
                }
            }
            else
            {
                MelonLogger.Msg("SELFTEST no MainMenu; using current scene.");
            }
            return;
        }

        if (testPhase == 1 && now >= testAt)
        {
            testSim = FindOne<GasSimulation>();
            if (testSim == null || testSim.Grid == null)
            {
                MelonLogger.Msg("SELFTEST waiting for sim...");
                testAt = now + 2f;
                return;
            }

            testGrid = testSim.Grid;
            testBase = SpeciesIndex(testSim, "Fuel");
            testExtra = SpeciesIndex(testSim, "Oxidizer");
            if (testBase < 0 || testExtra < 0)
            {
                MelonLogger.Error("SELFTEST species not found.");
                testPhase = 99;
                return;
            }

            testX = testGrid.Width / 2;
            testY = testGrid.Height / 2;
            testGenerator = new Generator
            {
                x = testX,
                y = testY,
                species = testBase,
                radius = 5f,
                rate = 40f,
                temperature = 300f,
                maxPressure = 0f,
                enabled = true,
                scriptId = 0
            };
            testGrid.Generators.Add(testGenerator);
            testOriginalCount = testGrid.Generators.Count;

            Mixes.Clear();
            Mix mix = new Mix { X = testX, Y = testY, BaseSpecies = testBase };
            mix.Extras.Add(new Extra { Species = testExtra, Share = 0.5f });
            Mixes[Key(testGenerator)] = mix;

            MelonLogger.Msg("SELFTEST generator added at " + testX + "," + testY
                + " base=" + testBase + " extra=" + testExtra);
            testPhase = 2;
            testAt = now + 2f;
            return;
        }

        if (testPhase == 2 && now >= testAt)
        {
            float baseAmount = 0f;
            float extraAmount = 0f;
            for (int y = testY - 5; y <= testY + 5; y++)
            {
                for (int x = testX - 5; x <= testX + 5; x++)
                {
                    if (x < 0 || y < 0 || x >= testGrid.Width || y >= testGrid.Height)
                    {
                        continue;
                    }
                    int cell = testGrid.Index(x, y);
                    int cellCount = testGrid.CellCount;
                    int speciesCount = testGrid.SpeciesCount;
                    baseAmount += testGrid.Amount[cell * speciesCount + testBase];
                    extraAmount += testGrid.Amount[cell * speciesCount + testExtra];
                }
            }

            MelonLogger.Msg("SELFTEST injection base=" + baseAmount.ToString("0.###")
                + " extra=" + extraAmount.ToString("0.###")
                + " generators=" + testGrid.Generators.Count
                + " expected=" + testOriginalCount);

            if (baseAmount > 0.05f && extraAmount > 0.05f)
            {
                float ratio = extraAmount / baseAmount;
                MelonLogger.Msg("SELFTEST ratio=" + ratio.ToString("0.000") + " (expected about 1.0)");
            }
            else
            {
                MelonLogger.Error("SELFTEST no gas detected.");
            }

            testPhase = 3;
            testAt = now;
            return;
        }

        if (testPhase == 3 && now >= testAt)
        {
            try
            {
                string dir = Path.Combine(MelonEnvironment.GameRootDirectory, "UserData", "GasMixTest");
                Directory.CreateDirectory(dir);
                bool saved = testSim.SaveTo(dir, "gasmix_selftest");
                MelonLogger.Msg("SELFTEST save=" + saved + " sidecar="
                    + File.Exists(Path.Combine(dir, "gasmix_selftest.engine.gasmix")));
                bool loaded = testSim.LoadFrom(dir, "gasmix_selftest");
                MelonLogger.Msg("SELFTEST load=" + loaded
                    + " generators=" + (testSim.Grid != null ? testSim.Grid.Generators.Count : -1)
                    + " mixes=" + Mixes.Count);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("SELFTEST save/load: " + ex);
            }
            testPhase = 4;
            testAt = now + 2f;
            MelonLogger.Msg("SELFTEST DONE (checking reload in 2s)");
        }

        if (testPhase == 4 && now >= testAt)
        {
            int count = testSim.Grid != null ? testSim.Grid.Generators.Count : -1;
            float again = 0f;
            if (testSim.Grid != null && count > 0)
            {
                Generator g = testSim.Grid.Generators[0];
                if (g != null)
                {
                    for (int y = g.y - 5; y <= g.y + 5; y++)
                    {
                        for (int x = g.x - 5; x <= g.x + 5; x++)
                        {
                            if (x < 0 || y < 0 || x >= testSim.Grid.Width || y >= testSim.Grid.Height) continue;
                            int cell = testSim.Grid.Index(x, y);
                            again += testSim.Grid.Amount[cell * testSim.Grid.SpeciesCount + testExtra];
                        }
                    }
                }
            }
            MelonLogger.Msg("SELFTEST reloaded mixes=" + Mixes.Count
                + " pending=" + Pending.Count
                + " generators=" + count
                + " extraGas=" + again.ToString("0.###"));
            testPhase = 5;
            testAt = now + 0.5f;
            return;
        }

        if (testPhase == 5 && now >= testAt)
        {
            GasGrid grid = testSim.Grid;
            if (grid != null && grid.Generators.Count > 0)
            {
                Generator source = grid.Generators[0];
                if (source != null && Mixes.TryGetValue(Key(source), out Mix sourceMix))
                {
                    int altA = (source.species + 1) % grid.SpeciesCount;
                    int altB = (source.species + 2) % grid.SpeciesCount;
                    Generator targetA = new Generator
                    {
                        x = source.x + 8,
                        y = source.y,
                        species = altA,
                        radius = 3f,
                        rate = 10f,
                        temperature = 300f,
                        maxPressure = 0f,
                        enabled = true
                    };
                    Generator targetB = new Generator
                    {
                        x = source.x + 16,
                        y = source.y,
                        species = altB,
                        radius = 3f,
                        rate = 10f,
                        temperature = 300f,
                        maxPressure = 0f,
                        enabled = true
                    };
                    grid.Generators.Add(targetA);
                    grid.Generators.Add(targetB);

                    int applied = ApplyMixToAll(grid, source, sourceMix);
                    bool ok = targetA.species == source.species
                        && targetB.species == source.species
                        && Mixes.ContainsKey(Key(targetA))
                        && Mixes.ContainsKey(Key(targetB));
                    MelonLogger.Msg("SELFTEST applyAll applied=" + applied
                        + " ok=" + ok
                        + " targetA=" + targetA.species
                        + " targetB=" + targetB.species
                        + " mixes=" + Mixes.Count);
                }
            }
            testPhase = 6;
        }
    }

    private static int SpeciesIndex(GasSimulation simulation, string name)
    {
        var species = simulation.Species;
        if (species == null)
        {
            return -1;
        }
        for (int i = 0; i < species.Length; i++)
        {
            SpeciesDef definition = species[i];
            if (definition != null && definition.name == name)
            {
                return i;
            }
        }
        return -1;
    }
}
#endif



