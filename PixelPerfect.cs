using HarmonyLib;
using FrooxEngine;
using ResoniteModLoader;
using System.Reflection;
using UnityFrooxEngineRunner;
using Camera = FrooxEngine.Camera;

namespace PixelPerfect;

public partial class PixelPerfect : ResoniteMod
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


        // Unfortunately, this needs to patch a compiler-generated type to alter the texture format before the render texture is created.
        // This will probably not work across large code changes and recompiles. TODO: Make this more robust and not terrible.
        MethodInfo RTCUpdate = 
            typeof(RenderTextureConnector)
            .GetNestedType("<>c__DisplayClass15_0", AccessTools.all)
            .GetMethod("<Update>b__0", AccessTools.all);


        // Patch it with "Update_Patch" transpiler from our patch class.
        MethodInfo Detour = AccessTools.Method(typeof(PixelPerfect_Patches), "Update_Patch");
        harmony.Patch(RTCUpdate, transpiler: new(Detour));


        Engine.Current.RunPostInit(() =>
        {
            // Add debug option for spawning ready-made capture device for testing.
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
}
