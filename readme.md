# Overview

This repo demonstrates interop between Vulkan and DirectX via embedding the former into WinUI 3 using [VK_KHR_external_memory_win32](https://registry.khronos.org/vulkan/specs/1.3-extensions/man/html/VK_KHR_external_semaphore_win32.html) extension. 

Example is written naive without any abstractions and mostly follows great [Alexander Overvoorde's vulkan tutorial](https://vulkan-tutorial.com) to create vulkan resources but excludes vulkan swapchain infrastructure and creates framebuffer using shared Direct3D 11 texture.

[Silk.NET](https://github.com/dotnet/Silk.NET) bindings are used for DirectX and Vulkan calling.

https://user-images.githubusercontent.com/59914970/224508276-392b1869-33c4-40c0-a3f8-0d9731c06f4c.mp4

# Interop process

We need to

1. Create DXGI swapchain and get texture from it

```csharp
var swapchainDesc1 = new SwapChainDesc1
{
    ...
    Width = width,
    Height = height,
    Scaling = Scaling.Stretch,
    BufferUsage = DXGI.UsageRenderTargetOutput
};

_ = factory2.CreateSwapChainForComposition(dxgiDevice3, swapchainDesc1, 
        default(ComPtr<IDXGIOutput>), ref swapchain1);

_ = swapchain1.GetBuffer(0, out colorTexture);
```

> **Note**
> 
> Width and height was received from WinUI SwapChainPanel element which accepts our swapchain

1. Create "render target" D3D texture in shared mode

```csharp
var renderTargetDescription = new Texture2DDesc
{
    ...
    Width = width,
    Height = height,
    BindFlags = (uint)BindFlag.RenderTarget,
    MiscFlags = (uint)ResourceMiscFlag.Shared
};

_ = device.Get().CreateTexture2D(ref renderTargetDescription, null, renderTargetTexture.GetAddressOf());
```

3. Query it to DXGI resource and get shared handle

```csharp
var guid = IDXGIResource.Guid;
ComPtr<IDXGIResource> resource = default;

_ = renderTargetTexture.Get().QueryInterface(ref guid, (void**)resource.GetAddressOf());

void* sharedHandle;
_ = resource.Get().GetSharedHandle(&sharedHandle);

sharedTextureHandle = (nint)sharedHandle;
```

4. On the vulkan side - create image using shared handle for memory importing, after this create view and framebuffer

```csharp
var externalMemoryImageInfo = new ExternalMemoryImageCreateInfo(
    handleTypes: ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit);

var imageCreateInfo = new ImageCreateInfo
{
    ...
    PNext = &externalMemoryImageInfo,
    SharingMode = SharingMode.Exclusive
};

vk.CreateImage(device, imageCreateInfo, null, out image).Check();

vk.GetImageMemoryRequirements(device, image, out var memoryRequirements);

var importMemoryInfo = new ImportMemoryWin32HandleInfoKHR
{
    ...
    HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit,
    Handle = directTextureHandle
};

var allocateInfo = new MemoryAllocateInfo
{
    ...
    PNext = &importMemoryInfo
};

vk.AllocateMemory(device, allocateInfo, null, out var deviceMemory).Check();
vk.BindImageMemory(device, image, deviceMemory, 0ul).Check();
```

When framebuffer is created, we are ready to interop - just render a frame and call DirectX to copy data from "render target" to the texture associated with swapchain and present.

```csharp
context.Get().CopyResource(colorResource.Handle, renderTargetResource.Handle);

_ = swapchain1.Get().Present(0u, (uint)SwapChainFlag.None);
```

We also need to handle resizing - when it occurs we need to recreate DirectX and Vulkan resources.

# Prerequisites
* .NET 7
* Windows SDK 10.0.22621
* Vulkan SDK (if you want compile shaders yourself)