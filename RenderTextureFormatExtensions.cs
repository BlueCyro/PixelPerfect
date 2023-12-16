using FrooxEngine;
using System.Collections.Concurrent;

namespace PixelPerfect;

public static class RenderTextureFormatExtensions
{
    internal static readonly ConcurrentDictionary<IRenderTextureConnector, bool> bgra_marked = new();


    /// <summary>
    /// Marks a texture connector to be BGRA-formatted next time its updated.
    /// </summary>
    /// <param name="connector">The connector to mark</param>
    public static void MarkBGRA(this IRenderTextureConnector connector)
    {
        PixelPerfect.Msg("Registering connector for BGRA!");
        bgra_marked.TryAdd(connector, true);
    }


    /// <summary>
    /// Unmarks a render texture connector, allowing it to reset back to ARGBHalf next time its updated.
    /// </summary>
    /// <param name="connector">The connector to unmark</param>
    public static void UnmarkBGRA(this IRenderTextureConnector connector)
    {
        PixelPerfect.Msg("Unregistering connector!");
        bgra_marked.TryRemove(connector, out _);
    }


    /// <summary>
    /// Checks if a particular connector is marked to be BGRA-formatted or not.
    /// </summary>
    /// <param name="connector">The connector to check</param>
    /// <returns></returns>
    public static bool IsBGRA(this IRenderTextureConnector connector)
    {
        return bgra_marked.ContainsKey(connector);
    }
}