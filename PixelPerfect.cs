using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using Camera = FrooxEngine.Camera;
using Texture2D = UnityEngine.Texture2D;
using UnityFrooxEngineRunner;
using System.Reflection.Emit;
using System.Reflection;
using UnityEngine;
using RenderTexture = UnityEngine.RenderTexture;
using System.Collections.Concurrent;

namespace PixelPerfect;

public class PixelPerfect : ResoniteMod
{
    public override string Name => "Pixel Perfect";
    public override string Author => "Cyro";
    public override string Version => "1.0.0";
    public override string Link => "???";
    public static ModConfiguration? Config;
    public static event EventHandler? Update;


    public override void OnEngineInit()
    {
        Harmony harmony = new("net.Cyro.PixelPerfect");
        Config = GetConfiguration();
        Config?.Save(true);
        harmony.PatchAll();

        MethodInfo RTCUpdate = typeof(RenderTextureConnector).GetNestedType("<>c__DisplayClass15_0", AccessTools.all).GetMethod("<Update>b__0", AccessTools.all);
        MethodInfo Detour = AccessTools.Method(typeof(PixelPerfect_Patches), "Update_Patch");
        harmony.Patch(RTCUpdate, transpiler: new(Detour));

        Engine.Current.RunPostInit(() =>
        {
            DevCreateNewForm.AddAction("NDI", "Capture camera", s =>
            {
                s.Tag = "PixelPerfect.CaptureDevice";
                var field = s.AttachComponent<ReferenceField<Camera>>();
                s.OpenInspectorForTarget();
                var cam = s.AttachComponent<Camera>();
                var tex = s.AttachComponent<RenderTextureProvider>();
                tex.Size.Value = new(512, 512);
                cam.RenderTexture.Target = tex;
            });
        });
    }

    [HarmonyPatch]
    public static class PixelPerfect_Patches
    {
        [HarmonyPatch(typeof(UpdateManager), "RunUpdates")]
        [HarmonyPostfix]
        public static void RunUpdates_Postfix()
        {
            Update?.Invoke(null, EventArgs.Empty);
        }


        [HarmonyPatch(typeof(RenderTextureProvider), "OnAwake")]
        [HarmonyPostfix]
        public static void OnAwake_Postfix(RenderTextureProvider __instance)
        {
            __instance.RunInUpdates(8, () =>
            {
                try
                {
                    ISyncMember? tagField = __instance.Slot.GetSyncMember("Tag");
                    if (tagField == null)
                        return;

                    void Changed(IChangeable c)
                    {
                        if (__instance.Slot.Tag == "PixelPerfect.CaptureDevice")
                            RenderCapturer.Register(__instance);
                        else
                            RenderCapturer.Unregister(__instance);
                    }

                    void Destroyed(IDestroyable d)
                    {
                        RenderCapturer.Unregister(__instance);
                        tagField.Changed -= Changed;
                        __instance.Slot.Destroyed -= Destroyed;
                    }

                    tagField.Changed += Changed;
                    __instance.Slot.Destroyed += Destroyed;
                    
                    if (__instance.Slot.Tag == "PixelPerfect.CaptureDevice")
                    {
                        RenderCapturer.Register(__instance);
                    }
                }
                catch (Exception e)
                {
                    PixelPerfect.Msg($"Exception initializing RenderCapturer for render texture: {e}");
                }
            });
        }




        // This is extremely very brittle and terrible. Don't do this if you can help it, kids.
        public static IEnumerable<CodeInstruction> Update_Patch(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var constructors = typeof(RenderTexture).GetConstructors();
            for (int i = 0; i < codes.Count - 1; i++)
            {
                CodeInstruction inst = codes[i];
                CodeInstruction next = codes[i + 1];
                
                if (inst.opcode == OpCodes.Ldc_I4_2 && 
                    next.opcode == OpCodes.Newobj &&
                    constructors.Contains(next.operand as ConstructorInfo)
                )
                {
                    PixelPerfect.Msg($"Found {codes[i]} at index {i}! Replacing with detour!");
                    var connectorInfo =
                        codes.Select(c => c.operand as FieldInfo)
                        .FirstOrDefault(f => f?.FieldType == typeof(RenderTextureConnector));
                    
                    if (connectorInfo == null)
                    {
                        PixelPerfect.Msg("Can't find connector reference! Aborting!");
                        return codes;
                    }
                    codes[i] = new(OpCodes.Call, typeof(PixelPerfect_Patches).GetMethod("Format_Detour"));
                    codes.InsertRange(i, new List<CodeInstruction>
                    {
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldfld, connectorInfo)
                    });
                    break;
                }
            }
            return codes;
        }

        public static RenderTextureFormat Format_Detour(RenderTextureConnector connector)
        {
            PixelPerfect.Msg($"Setting texture to: {(connector.IsMarked() ? "BGRA" : "ARGBHalf")}");
            return connector.IsMarked() ? RenderTextureFormat.BGRA32 : RenderTextureFormat.ARGBHalf;
        }
    }
}

public static class RenderTextureFormatExtensions
{
    internal static readonly ConcurrentDictionary<IRenderTextureConnector, bool> bgra_marked = new();

    public static void MarkBGRA(this IRenderTextureConnector connector)
    {
        PixelPerfect.Msg("Registering connector for BGRA!");
        bgra_marked.TryAdd(connector, true);
    }

    public static void Unmark(this IRenderTextureConnector connector)
    {
        PixelPerfect.Msg("Unregistering connector!");
        bgra_marked.TryRemove(connector, out _);
    }

    public static bool IsMarked(this IRenderTextureConnector connector)
    {
        return bgra_marked.ContainsKey(connector);
    }
}