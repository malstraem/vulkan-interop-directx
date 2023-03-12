# Overview

This repo demonstrates interop between Vulkan and DirectX via embedding the former into WinUI 3 using [VK_KHR_external_memory](https://registry.khronos.org/vulkan/specs/1.3-extensions/man/html/VK_KHR_external_memory.html) extension. Example is written naive without any abstractions and mostly follows great [Alexander Overvoorde's vulkan tutorial](https://vulkan-tutorial.com) to create vulkan resources.

But excludes vulkan swapchain infrastructure, example creates framebuffer using shared Direct3D 11 texture.

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
    SwapEffect = SwapEffect.FlipSequential,
    BufferUsage = DXGI.UsageRenderTargetOutput
};

_ = factory2.Get().CreateSwapChainForComposition((IUnknown*)dxgiDevice3.Handle, ref swapchainDesc1, null, swapchain1.GetAddressOf());

var guid = ID3D11Texture2D.Guid;
_ = swapchain1.Get().GetBuffer(0, ref guid, (void**)colorTexture.GetAddressOf());
```

>Width and height received from WinUI SwapChainPanel element which must accept our swapchain

2. Create "render target" D3D texture in shared mode

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

4. On the vulkan side create image using shared handle for memory importing, after this create imageview and framebuffer

```csharp
var externalMemoryImageInfo = new ExternalMemoryImageCreateInfo()
{
    ...
    HandleTypes = ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit
};

var imageCreateInfo = new ImageCreateInfo
{
    ...
    PNext = &externalMemoryImageInfo,
    SharingMode = SharingMode.Exclusive
};

Check(vk.CreateImage(device, &imageCreateInfo, null, out image));

vk.GetImageMemoryRequirements(device, image, out var memoryRequirements);

var importMemoryInfo = new ImportMemoryWin32HandleInfoKHR
{
    ...
    HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit,
    Handle = directTextureHandle
};

var allocInfo = new MemoryAllocateInfo
{
    ...
    PNext = &importMemoryInfo
};

Check(vk.AllocateMemory(device, &allocInfo, null, out var deviceMemory));
Check(vk.BindImageMemory(device, image, deviceMemory, 0ul));
```

Now, when we created framebuffer we are ready for interop - just render a frame and call DirectX to copy data from "render target" to texture associated with swapchain and present it.

```csharp
context.Get().CopyResource(colorResource.Handle, renderTargetResource.Handle);

_ = swapchain1.Get().Present(0u, (uint)SwapChainFlag.None);
```

We also need to handle resizing - when it occurs we need to recreate DirectX and Vulkan resources.

# Prerequisites
* .NET 7
* Windows SDK 10.0.22621
* Vulkan SDK (in the fact not necessaraly while you don't want compile shader yourself, repo contains  `.spv` files and comment build instractions in csproj.