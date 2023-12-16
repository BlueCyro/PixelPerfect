using NewTek;
using NewTek.NDI;
using FrooxEngine;
using UnityEngine.Rendering;
using UnityFrooxEngineRunner;
using System.Collections.Concurrent;
using Unity.Collections.LowLevel.Unsafe;
using RenderTexture = UnityEngine.RenderTexture;

namespace PixelPerfect;

public class RenderCapturer : IDisposable
{
    // Max 5 frames processed at a time. Can get jerky if the frames aren't coming in fast enough.
    public const int MAX_FRAME_PROCESS_COUNT = 5;
    public static readonly Dictionary<RenderTextureProvider, RenderCapturer> caps = new();
    
    

    private readonly Sender sender;
    private readonly RenderTextureProvider provider;
    private RenderTexture? lastTex;
    private Task frameProcessor;
    private readonly AutoResetEvent ticker = new(false);
    private BlockingCollection<(AsyncGPUReadbackRequest req, RenderTexture tex, int width, int height)> queuedFrames = new(/* MAX_FRAME_PROCESS_COUNT */);

    

    public int Width => provider.Size.Value.X;
    public int Height => provider.Size.Value.Y;



    private RenderCapturer(RenderTextureProvider tex)
    {
        PixelPerfect.Msg($"Initializing capture device on slot: \"{tex.Slot.Name}\"!");


        // Store the provider and make a new NDI sender.
        provider = tex;
        sender = new(tex.Slot.Name, false);

        PixelPerfect.Msg($"Registering frame from buffer ({Width}, {Height})");


        if (TryGetRenderTexture(out var curTex))
            lastTex = curTex;


        // Make a new frame processor, subscribe to the update event, and make sure to dispose the object if the provider is destroyed.
        frameProcessor = Task.Run(ProcessFrames);
        PixelPerfect.Update += UpdateNDI;
        tex.Destroyed += d => Dispose();
    }



    /// <summary>
    /// Registers a texture provider for frame capture and sets it to the proper pixel format.
    /// </summary>
    /// <param name="prov"></param>
    public static void Register(RenderTextureProvider prov)
    {
        var asset = prov.Asset;
        asset?.Connector?.MarkBGRA();
        prov.MarkChangeDirty();
        caps.Add(prov, new(prov));
    }



    /// <summary>
    /// Unregisters a render texture for frame capture and sets it back to the correct format.
    /// </summary>
    /// <param name="prov"></param>
    public static void Unregister(RenderTextureProvider prov)
    {
        if (caps.TryGetValue(prov, out RenderCapturer cap))
        {
            cap.Dispose();
        }
    }



    /// <summary>
    /// Asynchronously processes each frame in the order it was queued.
    /// </summary>
    private unsafe void ProcessFrames()
    {
        try
        {
            while (!queuedFrames.IsAddingCompleted)
            {
                // Take from the queue
                var (req, tex, width, height) = queuedFrames.Take();
                
                
                // Block the thread on the current request until it's done to ensure order.
                while (!req.done)
                {
                    ticker.WaitOne(); // Wait for a tick from the update loop.
                    if (queuedFrames.IsAddingCompleted)
                        return; // Just abort if the adding is completed at this stage.
                }

                
                // Make sure the texture isn't null and isn't different from the old one, this indicates that the data is probably bad.
                if (!TryGetRenderTexture(out RenderTexture curTex) || curTex != tex)
                    continue;
                
                if (req.hasError) // Just continue, this has no verbose response so I can't even print the error anyways. It seems to work fine. I think.
                    continue;

                

                // Get the data and some scary pointers to send the frame.
                using var buf = req.GetData<byte>();
                IntPtr ptr = (IntPtr)buf.GetUnsafePtr();
                int stride = (width * 32 + 7) / 8;

                

                // New frame with bogus frame timing since this is unclocked.
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
                // Send the frame. The 'using' statements above will ensure the allocated resources are freed once the scope ends.
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



    /// <summary>
    /// Update loop event for updating the NDI stream each frame.
    /// </summary>
    /// <param name="s">Nothing</param>
    /// <param name="args">Empty</param>
    private void UpdateNDI(object? s, EventArgs args)
    {
        // Hard clamping to no more pixels than the equivelant of 1920x1080. Any more is impractical for realtime CPU encoding in this setup. :/
        if (Width * Height > 2073600)
            return;
        
        ticker.Set(); // Nudge the ticker in case the send loop is stalled waiting for the next request.
        try
        {
            if (TryGetRenderTexture(out var tex)) // Make sure the texture actually exists
            {
                if (lastTex != tex)
                {
                    // Screech to a halt if the render texture is mismatched. We've gotta clear everything out otherwise a bunch of
                    // terrible corruption happens and it all explodes. Badly.
                    PixelPerfect.Msg($"Pixel buffer mismatch! Registering new buffer of size: {Width}, {Height}");
                    RegisterBuffer();
                    return;
                }

                // Add a frame to the queue where it will used as it's completed.
                queuedFrames.Add((AsyncGPUReadback.Request(tex, 0), tex, Width, Height));
            }
        }
        catch (Exception ex)
        {
            PixelPerfect.Msg($"Error in NDI update loop! Exception: {ex}");
        }
    }



    /// <summary>
    /// Tries to get the render texture, if any.
    /// </summary>
    /// <param name="tex">The texture, if retrieved.</param>
    /// <returns></returns>
    public bool TryGetRenderTexture(out RenderTexture tex)
    {
        tex = (provider.Asset?.Connector as RenderTextureConnector)?.RenderTexture!;
        return tex != null;
    }



    /// <summary>
    /// Halts all activity, then registers a new buffer and frame queue to avoid corruption.
    /// </summary>
    private void RegisterBuffer()
    {
        if (TryGetRenderTexture(out var curTex))
        {
            // Complete the adding and nudge the ticker so that if the thread is stalled, it will return when it sees that the collection is finished.
            queuedFrames.CompleteAdding();
            ticker.Set();


            // Wait for the frame processor
            try
            {
                frameProcessor.Wait();
            }
            catch(AggregateException agx)
            {
                PixelPerfect.Msg($"Exception when waiting for the frame processor to halt! Exception: {agx}");
            }


            // Make a new frame queue and restart the frame processor.
            queuedFrames = new BlockingCollection<(AsyncGPUReadbackRequest req, RenderTexture tex, int width, int height)>(/* MAX_FRAME_PROCESS_COUNT */);
            frameProcessor = Task.Run(ProcessFrames);
            lastTex = curTex;
        }
    }
    


    /// <summary>
    /// Disposes of all managed resources contained in this object.
    /// </summary>
    public void Dispose()
    {
        // Unsubscribe the update loop
        PixelPerfect.Update -= UpdateNDI;

        // Halt the frame queue to cancel/abort the task and nudge the frame processor to allow for completion.
        queuedFrames.CompleteAdding();
        ticker.Set();

        // Wait for the processor to finish.
        try
        {
            frameProcessor.Wait();
        }
        catch(AggregateException ex)
        {
            ex.Handle(e => e is TaskCanceledException);
        }
        finally // Dispose of a bunch of garbage now.
        {
            sender.Dispose();
            provider.Asset?.Connector?.UnmarkBGRA();
            provider.MarkChangeDirty();
            caps.Remove(provider);
            PixelPerfect.Msg("Render capturer disposed of successfully!");
        }
    }
}