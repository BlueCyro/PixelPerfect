using HarmonyLib;
using FrooxEngine;
using UnityEngine;
using System.Reflection;
using UnityFrooxEngineRunner;
using System.Reflection.Emit;
using RenderTexture = UnityEngine.RenderTexture;

namespace PixelPerfect;

public partial class PixelPerfect
{

    // A class to hold all of the patches.
    [HarmonyPatch]
    public static class PixelPerfect_Patches
    {
        public const string CAPTURE_DEVICE_MARKER = "PixelPerfect.CaptureDevice";



        // I couldn't seem to find any "Update" event, so I made my own.
        [HarmonyPatch(typeof(UpdateManager), "RunUpdates")]
        [HarmonyPostfix]
        public static void RunUpdates_Postfix()
        {
            Update?.Invoke(null, EventArgs.Empty);
        }



        // Patch OnAwake for the provider so that it can dynamically listen to changes and update accordingly.
        [HarmonyPatch(typeof(RenderTextureProvider), "OnAwake")]
        [HarmonyPostfix]
        public static void OnAwake_Postfix(RenderTextureProvider __instance)
        {
            __instance.RunInUpdates(8, () =>
            {
                try
                {
                    // Try to get the "Tag" sync member since its protected.
                    ISyncMember? tagField = __instance.Slot.GetSyncMember("Tag");

                    // Fail if null
                    if (tagField == null)
                        return;

                    // Change handler to register and unregister
                    void TagChanged(IChangeable c)
                    {
                        if (__instance.Slot.Tag == CAPTURE_DEVICE_MARKER)
                            RenderCapturer.Register(__instance);
                        else
                            RenderCapturer.Unregister(__instance);
                    }

                    // Change handler to update the NDI name.
                    void NameChanged(IChangeable c)
                    {
                        RenderCapturer.Unregister(__instance);
                        if (__instance.Slot.Tag == CAPTURE_DEVICE_MARKER)
                            RenderCapturer.Register(__instance);
                    }


                    // Be a good boy and unsubscribe all of our events on destroy.
                    void Destroyed(IDestroyable d)
                    {
                        RenderCapturer.Unregister(__instance);
                        tagField.Changed -= TagChanged;
                        __instance.Slot.NameChanged -= NameChanged;
                        __instance.Slot.Destroyed -= Destroyed;
                    }


                    tagField.Changed += TagChanged;
                    __instance.Slot.Destroyed += Destroyed;
                    __instance.Slot.NameChanged += NameChanged;
                    
                    // Immediately check if the provider should be a capture device.
                    if (__instance.Slot.Tag == CAPTURE_DEVICE_MARKER)
                        RenderCapturer.Register(__instance);
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
            // Convert the codes to a list so we can manipulate them more easily.
            // Also get the constructors of render texture, we'll need those later.
            var codes = instructions.ToList();
            var constructors = typeof(RenderTexture).GetConstructors();

            // Iterate the instructions.
            for (int i = 0; i < codes.Count - 1; i++)
            {
                CodeInstruction inst = codes[i];
                CodeInstruction next = codes[i + 1];
                

                // Check if the current opcode loads 2 (ARGBHalf) and the next opcode instantiates the unity RenderTexture.
                // This can just be lazy and check if the the operand is a constructor that belongs to RenderTexture.
                if (inst.opcode == OpCodes.Ldc_I4_2 && 
                    next.opcode == OpCodes.Newobj &&
                    constructors.Contains(next.operand as ConstructorInfo)
                )
                {
                    PixelPerfect.Msg($"Found {codes[i]} at index {i}! Replacing with detour!");

                    // Find "this" in the nested compiler-generated type. It should at least be resilient to recompiles.
                    var connectorInfo =
                        codes.Select(c => c.operand as FieldInfo)
                        .FirstOrDefault(f => f?.FieldType == typeof(RenderTextureConnector));
                    
                    if (connectorInfo == null)
                    {
                        PixelPerfect.Msg("Can't find connector reference! Aborting!");
                        return codes;
                    }

                    // If all is well, replace the 2 (ARGBHalf) with a call to the format detour. This will check if the texture should be BGRA32 or ARGBHalf.
                    codes[i] = new(OpCodes.Call, typeof(PixelPerfect_Patches).GetMethod("Format_Detour"));
                    codes.InsertRange(i, new List<CodeInstruction> // This technically inserts these before our call, so we can pass in the connector.
                    {
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldfld, connectorInfo)
                    });
                    break;
                }
            }
            return codes;
        }


        // This will check to see if the connector is marked to be created as BGRA, or ARGBHalf (the default).
        // This is so we can avoid expensive blitting at higher resolutions since the camera will be in the format that's needed already.
        public static RenderTextureFormat Format_Detour(RenderTextureConnector connector)
        {
            PixelPerfect.Msg($"Setting texture to: {(connector.IsBGRA() ? "BGRA" : "ARGBHalf")}");
            return connector.IsBGRA() ? RenderTextureFormat.BGRA32 : RenderTextureFormat.ARGBHalf;
        }
    }
}
