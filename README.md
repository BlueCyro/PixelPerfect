# ✨🎨 Pixel Perfect: Capture your moment, perfectly

Pixel Perfect is a mod for [Resonite](https://resonite.com) via [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) which allows you to output pixel buffers directly from render rextures out of the game via [NDI](https://en.wikipedia.org/wiki/Network_Device_Interface). This output can be directly used in software like [OBS](https://obsproject.com/) directly!

# Installation

- Download the [NDI SDK](https://ndi.video/download-ndi-sdk/) and compile the "NDILibDotNet2" project and drop the result in your rml_libs folder.
- Copy all of the 3 dlls from `Bin/x64` in the root of the SDK to `Resonite_Data/Plugins/x86_64`
- Compile PixelPerfect and place it in rml_mods

# Usage

In your "Create New" menu on the developer tool, you will see a new option called "NDI". This will create a template "Capture camera" which will automatically start as a capture device at 512x512.

To instantiate a capture device:

- Attach a RenderTextureProvider to a slot and make sure the tag is set to `PixelPerfect.CaptureDevice`.
- Name the slot whatever you wish - this will automatically regenerate the capture device each time the slot name is changed.
- If using OBS, make sure you have [obs-ndi](https://obsproject.com/forum/resources/obs-ndi-newtek-ndi%E2%84%A2-integration-into-obs-studio.528/) installed, then simply make a new NDI source.

# DISCLAIMER!

**This project is still very much under development, and as a result implementation details could change at any time!**

Furthermore, the resolution is clamped to the equivelant pixel count of 1920 * 1080, as any more causes stuttering and crashes. Hopefully this is remediable in the future.