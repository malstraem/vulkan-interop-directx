# Overview

This repo demonstrates interop between Vulkan and DirectX via embedding the former into WinUI 3 using [VK_KHR_external_memory_win32](https://registry.khronos.org/vulkan/specs/1.3-extensions/man/html/VK_KHR_external_memory_win32.html) extension. It's basically follows great [Alexander Overvoorde's tutorial](https://vulkan-tutorial.com) for creating resources, but excludes swapchain infrastructure and creates a framebuffer using a shared Direct3D 11 texture.

Whether you are using your own or third party abstractions - it should be easy to adapt the code for use.

The example is written naively, step by step - see [VulkanInterop.Initialize](source/VulkanInterop.cs#L481).

[Silk.NET](https://github.com/dotnet/Silk.NET) - bindings used for DirectX and Vulkan calls.

[Damaged Helmet](https://sketchfab.com/3d-models/battle-damaged-sci-fi-helmet-pbr-b81008d513954189a063ff901f7abfe4) - model used as an example. [SharpGLTF](https://github.com/vpenades/SharpGLTF) - loader used to read the model.

https://github.com/malstraem/vulkan-interop-directx/assets/59914970/c08e451d-378a-4d47-a0ee-46e75faabb58

# Interop process

We need to

1. Create DXGI swapchain and get the texture.

```csharp
var swapchainDescription = new SwapChainDesc1
{
    ...
    Width = width,
    Height = height,
    SwapEffect = SwapEffect.FlipSequential,
    BufferUsage = DXGI.UsageRenderTargetOutput
};

_ = factory.CreateSwapChainForComposition(dxgiDevice, swapchainDescription, default(ComPtr<IDXGIOutput>), ref swapchain);

colorTexture = swapchain.GetBuffer<ID3D11Texture2D>(0u);
```

> **Width and height was received from WinUI SwapChainPanel element which accepts our swapchain**

2. Create "render target" D3D texture in shared mode.

```csharp
var renderTargetDescription = new Texture2DDesc
{
    ...
    Width = width,
    Height = height,
    BindFlags = (uint)BindFlag.RenderTarget,
    MiscFlags = (uint)ResourceMiscFlag.Shared
};

_ = device.CreateTexture2D(renderTargetDescription, null, ref renderTargetTexture);
```

3. Query it to DXGI resource and get shared handle.

```csharp
var resource = renderTargetTexture.QueryInterface<IDXGIResource>();

void* sharedHandle;
_ = resource.GetSharedHandle(&sharedHandle);

sharedTextureHandle = (nint)sharedHandle;

resource.Dispose();
```

4. On the Vulkan side - create an image using a shared handle for memory import, then create a view and a framebuffer. See [VulkanInterop.CreateImageViews](source/VulkanInterop.cs#L271).

```csharp
var externalMemoryImageInfo = new ExternalMemoryImageCreateInfo(handleTypes: ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit);

var imageInfo = new ImageCreateInfo
(
    ...
    format: Format.B8G8R8A8Unorm,
    usage: ImageUsageFlags.ColorAttachmentBit,
    pNext: &externalMemoryImageInfo
);

var importMemoryInfo = new ImportMemoryWin32HandleInfoKHR(handleType: ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit, handle: directTextureHandle);
var memoryInfo = new MemoryAllocateInfo(pNext: &importMemoryInfo);

vk.CreateImage(device, imageInfo, null, out directImage).Check();

vk.AllocateMemory(device, memoryInfo, null, out directImageMemory).Check();
vk.BindImageMemory(device, directImage, directImageMemory, 0ul).Check();
```

5. Once the framebuffer is created, we are ready to interop - just render a frame and then call DirectX to copy data from "render target" to the texture associated with the swapchain and present it.

```csharp
context.CopyResource(colorResource, renderTargetResource);
_ = swapchain.Present(0u, (uint)SwapChainFlag.None);
```

We also need to handle resizing - when it occurs we should recreate DirectX and Vulkan resources.

# Prerequisites and build

* .NET 7
* [Windows SDK 10.0.22621](https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/)

The example is `dotnet build` ready.
