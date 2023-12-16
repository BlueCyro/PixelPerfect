using FrooxEngine;
using NewTek.NDI;
using NewTek;
using UnityEngine;
using RenderTexture = UnityEngine.RenderTexture;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityFrooxEngineRunner;
using System.Collections.Concurrent;

namespace PixelPerfect;

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
    private readonly AutoResetEvent ticker = new(false);

    
    public int Width => provider.Size.Value.X;
    public int Height => provider.Size.Value.Y;

    
    private readonly object lockObj = new object();
    private BlockingCollection<(AsyncGPUReadbackRequest req, RenderTexture tex, int width, int height)> queuedFrames = new(/* MAX_FRAME_PROCESS_COUNT */);


    private RenderCapturer(RenderTextureProvider tex)
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
        var asset = prov.Asset;
        asset?.Connector?.MarkBGRA();
        prov.MarkChangeDirty();
        caps.Add(prov, new(prov));
    }

    public static void Unregister(RenderTextureProvider prov)
    {
        if (caps.TryGetValue(prov, out RenderCapturer cap))
        {
            cap.Dispose();
        }
    }

    private unsafe void ProcessFrames()
    {
        try
        {
            while (!queuedFrames.IsAddingCompleted)
            {
                var (req, tex, width, height) = queuedFrames.Take();
                
                while (!req.done)
                {
                    ticker.WaitOne();
                    if (queuedFrames.IsAddingCompleted)
                        return;
                }
 
                if (!TryGetRenderTexture(out RenderTexture curTex) || curTex != tex)
                    continue;
                
                if (req.hasError)
                {
                    // PixelPerfect.Msg($"Error with request: {req}");
                    continue;
                }

                using var buf = req.GetData<byte>();
                IntPtr ptr = (IntPtr)buf.GetUnsafePtr();
                int stride = (width * 32 + 7) / 8;

                using VideoFrame frame = new
                (
                    ptr,
                    width,
                    height,
                    stride,
                    NDIlib.FourCC_type_e.FourCC_type_BGRA,
                    (float)width / height,
                    60,
                    1,
                    NDIlib.frame_format_type_e.frame_format_type_progressive
                );
                sender.Send(frame);
            }
            PixelPerfect.Msg("Frame processing cancelled gracefully!");
        }
        catch (OperationCanceledException ex)
        {
            PixelPerfect.Msg($"Frame processing cancelled! Message: {ex.Message}");
        }
        catch (Exception ex)
        {
            PixelPerfect.Msg($"Frame processing FAILED!!! Exception: {ex}");
        }
    }

    public void UpdateNDI(object? s, EventArgs args)
    {
        ticker.Set();
        try
        {
            if (TryGetRenderTexture(out var tex))
            {
                if (pixelBuffer.width * pixelBuffer.height * 4 != Width * Height * 4)
                {
                    PixelPerfect.Msg($"Pixel buffer mismatch! Registering new buffer of size: {Width}, {Height}");
                    RegisterBuffer();
                    return;
                }

                lock (lockObj)
                {
                    queuedFrames.Add((AsyncGPUReadback.Request(tex, 0), tex, Width, Height));
                }
            }
        }
        catch (Exception ex)
        {
            PixelPerfect.Msg($"Error in NDI update loop! Exception: {ex}");
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
            ticker.Set();
            frameProcessor.Wait();

            pixelBuffer.Release();
            pixelBuffer = new(Width, Height, 0, RenderTextureFormat.BGRA32);

            queuedFrames = new BlockingCollection<(AsyncGPUReadbackRequest req, RenderTexture tex, int width, int height)>(/* MAX_FRAME_PROCESS_COUNT */);
            frameProcessor = Task.Run(ProcessFrames, tkSrc.Token);
        }
    }
    
    public void Dispose()
    {
        queuedFrames.CompleteAdding();
        tkSrc.Cancel();

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
        finally
        {
            tkSrc.Dispose();
            sender.Dispose();
            provider.Asset?.Connector?.Unmark();
            PixelPerfect.Msg("Render capturer disposed of successfully!");
        }
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