using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using NewTek.NDI;
using NewTek;
using UnityEngine;
using Camera = FrooxEngine.Camera;
using Texture2D = UnityEngine.Texture2D;
using RenderTexture = UnityEngine.RenderTexture;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityFrooxEngineRunner;
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

        Engine.Current.RunPostInit(() =>
        {
            DevCreateNewForm.AddAction("NDI", "Capture camera", s =>
            {
                s.Tag = "PixelPerfect.CaptureDevice";
                var field = s.AttachComponent<ReferenceField<Camera>>();
                s.OpenInspectorForTarget();
                var cam = s.AttachComponent<Camera>();
                var tex = s.AttachComponent<RenderTextureProvider>();
                tex.Depth.Value = 32;
                tex.Size.Value = new(512, 512);
                cam.RenderTexture.Target = tex;
            });
        });
    }

    [HarmonyPatch]
    public static class Patch_RunUpdates
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
            __instance.RunSynchronously(() =>
            {
                try
                {
                    if (__instance.Slot.Tag == "PixelPerfect.CaptureDevice")
                        RenderCapturer.Register(__instance);
                }
                catch (Exception e)
                {
                    PixelPerfect.Msg($"Exception initializing RenderCapturer for render texture: {e}");
                }
            });
        }
    }
}

public class RenderCapturer : IDisposable
{
    // Max 5 frames processed at a time.
    public const int MAX_FRAME_PROCESS_COUNT = 5;
    public static readonly Dictionary<RenderTextureProvider, RenderCapturer> caps = new();
    
    
    private readonly Sender sender;
    private readonly RenderTextureProvider provider;
    private RenderTexture pixelBuffer;
    private Task frameProcessor;
    private readonly CancellationTokenSource tkSrc = new();

    
    public int Width => provider.Size.Value.X;
    public int Height => provider.Size.Value.Y;

    
    private readonly object lockObj = new object();
    private BlockingCollection<(VideoFrame frame, NativeArray<byte> buffer)> queuedFrames = new(MAX_FRAME_PROCESS_COUNT);


    public RenderCapturer(RenderTextureProvider tex)
    {
        string name = tex.ReferenceID.ToString();
        PixelPerfect.Msg($"Initializing capture device \"{tex.Slot.Name}\"!");
        provider = tex;
        sender = new(tex.Slot.Name, false);
        PixelPerfect.Msg($"Registering frame from buffer ({Width}, {Height})");

        pixelBuffer = new(Width, Height, 0, RenderTextureFormat.BGRA32);
        // buf = new(Width * Height * 4, Allocator.Persistent);

        frameProcessor = Task.Run(ProcessFrames, tkSrc.Token);
        PixelPerfect.Update += UpdateNDI;
        tex.Destroyed += d => Dispose();

    }

    public static void Register(RenderTextureProvider prov)
    {
        caps.Add(prov, new(prov));
    }

    private void ProcessFrames()
    {
        try
        {
            foreach (var (frame, buffer) in queuedFrames.GetConsumingEnumerable())
            {
                lock (lockObj)
                {
                    sender.Send(frame);
                    buffer.Dispose();
                }
            }
            PixelPerfect.Msg("Frame processing cancelled gracefully!");
        }
        catch (OperationCanceledException ex)
        {
            PixelPerfect.Msg($"Frame processing cancelled! Message: {ex.Message}");
        }
    }

    public void UpdateNDI(object? s, EventArgs args)
    {
        if (TryGetRenderTexture(out var tex) && provider.Depth.Value == 32)
        {
            if (pixelBuffer.width * pixelBuffer.height * 4 != Width * Height * 4)
            {
                PixelPerfect.Msg($"Pixel buffer mismatch! Registering new buffer of size: {Width}, {Height}");
                RegisterBuffer();
                return;
            }

            lock (lockObj)
            {
                Graphics.Blit(tex, pixelBuffer);
                AsyncGPUReadback.Request(pixelBuffer, 0, req => Callback(req, tex));
            }
        }
    }

    public bool TryGetRenderTexture(out RenderTexture tex)
    {
        tex = (provider.Asset?.Connector as RenderTextureConnector)?.RenderTexture!;
        return tex != null;
    }

    public void RegisterBuffer()
    {
        lock (lockObj)
        {
            queuedFrames.CompleteAdding();
            frameProcessor.Wait();

            pixelBuffer.Release();
            pixelBuffer = new(Width, Height, 0, RenderTextureFormat.BGRA32);

            queuedFrames = new BlockingCollection<(VideoFrame frame, NativeArray<byte> buffer)>(MAX_FRAME_PROCESS_COUNT);
            frameProcessor = Task.Run(ProcessFrames, tkSrc.Token);
            // buf.Dispose();
            // buf = new(Width * Height * 4, Allocator.Persistent);
        }
    }

    private unsafe void Callback(AsyncGPUReadbackRequest req, RenderTexture tex)
    {
        lock (lockObj)
        {
            if (!TryGetRenderTexture(out var curTex) || curTex != tex || queuedFrames.Count == MAX_FRAME_PROCESS_COUNT)
                return;
            
            var buf = req.GetData<byte>();
            IntPtr ptr = (IntPtr)buf.GetUnsafePtr();
            int stride = (Width * 32 + 7) / 8;

            using VideoFrame frame = new
            (
                ptr,
                Width,
                Height,
                stride,
                NDIlib.FourCC_type_e.FourCC_type_BGRA,
                (float)Width / Height,
                60,
                1,
                NDIlib.frame_format_type_e.frame_format_type_progressive
            );
            queuedFrames.Add((frame, buf));
        }
    }

    public void Dispose()
    {
        tkSrc.Cancel();
        queuedFrames.CompleteAdding();

        PixelPerfect.Update -= UpdateNDI;
        pixelBuffer.Release();
        caps.Remove(provider);
        
        try
        {
            frameProcessor.Wait();
        }
        catch(AggregateException ex)
        {
            ex.Handle(e => e is TaskCanceledException);
        }
        tkSrc.Dispose();
    }
}

/*
public static class VideoSender
{
    public static bool init = false;
    private static readonly Sender sender = new("Pixel-Perfect", false);
    private static RenderTexture? temp;
    private static RenderTexture? from;
    private static NativeArray<byte> buf;
    private static readonly object _lock = new();

    public static void SetTexture(UnityEngine.Camera cam)
    {
        lock (_lock)
        {
            var tex = cam.targetTexture;
            from = tex;
            temp = new(tex.width, tex.height, 32, RenderTextureFormat.BGRA32);
            
            buf = new(tex.width * tex.height * 4, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }
    }

    public static void UnsetTexture()
    {
        lock (_lock)
        {
            buf.Dispose();
            temp = null;
        }
    }

    public static void SendFrame()
    {
        // PixelPerfect.Msg("Setting render texture");

        // ScreenCapture.CaptureScreenshotIntoRenderTexture(temp);

        // PixelPerfect.Msg("Requesting GPU readback...");
        lock (_lock)
        {
            if (temp == null)
                return;
            
            Graphics.Blit(from, temp);
            AsyncGPUReadback.RequestIntoNativeArray(ref buf, temp, 0, Callback);
            // flag = true;
        }

        // PixelPerfect.Msg("Setting render texture back");
    }
    public static void GPURequest(AsyncGPUReadbackRequest req)
    {
        if (req.hasError)
        {
            PixelPerfect.Msg("Async GPU readback failed!");
            return;
        }
        Task.Run(() => Callback(req)).ConfigureAwait(false);
    }

    public static unsafe void Callback(AsyncGPUReadbackRequest req)
    {
        lock (_lock)
        {
            if (temp == null)
                return;
            
            IntPtr ptr = (IntPtr)req.GetData<byte>().GetUnsafePtr();
            int stride = (temp.width * 32 + 7) / 8;

            using VideoFrame frame = new
            (
                ptr,
                temp.width,
                temp.height,
                stride,
                NDIlib.FourCC_type_e.FourCC_type_BGRA,
                (float)temp.width / temp.height,
                60,
                1,
                NDIlib.frame_format_type_e.frame_format_type_progressive
            );

            sender.Send(frame);
        }
    }
}
*/