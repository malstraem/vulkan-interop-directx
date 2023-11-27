# Overview

This repo demonstrates interop between Vulkan and DirectX via embedding the former into WinUI 3 and WPF using [VK_KHR_external_memory_win32](https://registry.khronos.org/vulkan/specs/1.3-extensions/man/html/VK_KHR_external_memory_win32.html) extension. It's basically follows great [Alexander Overvoorde's tutorial](https://vulkan-tutorial.com) for creating resources, but excludes swapchain infrastructure and creates a framebuffer using a shared Direct3D 11 texture.

Whether you are using your own or third party abstractions - it should be easy to adapt the code for use.

The example is written naively, step by step - see [VulkanInterop.Initialize](source/VulkanInterop.cs#L481) and [Window code behind](samples/MainWindow.xaml.cs).

[Silk.NET](https://github.com/dotnet/Silk.NET) - bindings used for DirectX and Vulkan calls.

[Damaged Helmet](https://sketchfab.com/3d-models/battle-damaged-sci-fi-helmet-pbr-b81008d513954189a063ff901f7abfe4) - model used as an example. [SharpGLTF](https://github.com/vpenades/SharpGLTF) - loader used to read the model.

https://github.com/malstraem/vulkan-interop-directx/assets/59914970/c08e451d-378a-4d47-a0ee-46e75faabb58

# Interop process

We need to

### 1. Prepare the back buffer texture

In case of WinUI - create a DXGI swapchain, get the texture and set the swapchain to the WinUI SwapChainPanel.

```csharp
var swapchainDescription = new SwapChainDesc1
{
    ...
    Width = width,
    Height = height,
    Format = Format.FormatR8G8B8A8Unorm,
    SwapEffect = SwapEffect.FlipSequential,
    SampleDesc = new SampleDesc(1u, 0u),
    BufferUsage = DXGI.UsageBackBuffer
};

ThrowHResult(dxgiFactory.CreateSwapChainForComposition
(
    dxgiDevice,
    swapchainDescription,
    default(ComPtr<IDXGIOutput>),
    ref swapchain
));

backbufferTexture = swapchain.GetBuffer<ID3D11Texture2D>(0u);

target.As<ISwapChainPanelNative>().SetSwapChain(swapchain);
```

In case of WPF - create a D3D9 texture and get the surface that will be used with WPF D3DImage.

```csharp
void* d3d9shared = null;
ThrowHResult(d3d9device.CreateTexture
(
    width,
    height,
    1u,
    D3D9.UsageRendertarget,
    Silk.NET.Direct3D9.Format.X8R8G8B8,
    Pool.Default,
    ref backbufferTexture,
    ref d3d9shared
));

ThrowHResult(backbufferTexture.GetSurfaceLevel(0u, ref surface));
```

### 2. Create a render target D3D11 texture in shared mode

In case of WinUI - this is regular D3D11 texture.

```csharp
var renderTargetDescription = new Texture2DDesc
{
    ...
    Width = width,
    Height = height,
    BindFlags = (uint)BindFlag.RenderTarget,
    MiscFlags = (uint)ResourceMiscFlag.Shared
};

ThrowHResult(d3d11device.CreateTexture2D(renderTargetDescription, null, ref renderTargetTexture));
```

With WinUI we also need to query both back buffer and render target textures to D3D11 resources for future copy operations.

```csharp
backbufferResource = backbufferTexture.QueryInterface<ID3D11Resource>();
renderTargetResource = renderTargetTexture.QueryInterface<ID3D11Resource>();
```

In case of WPF, we get the texture using shared handle of the D3D9 texture we created earlier.

```csharp
renderTargetTexture = d3d11device.OpenSharedResource<ID3D11Texture2D>(d3d9shared);
```

### 3. Query D3D11 render target texture to the DXGI resource and get a shared handle

```csharp
void* handle;

var resource = renderTargetTexture.QueryInterface<IDXGIResource>();
ThrowHResult(resource.GetSharedHandle(&handle));
resource.Dispose();

renderTargetSharedHandle = (nint)handle;
```

### 4. On the Vulkan side - create an image using the previously obtained shared handle

Then create the view and framebuffer - see [VulkanInterop.CreateImageViews](source/VulkanInterop.cs#L271).

```csharp
var externalMemoryImageInfo = new ExternalMemoryImageCreateInfo
(
    handleTypes: ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit
);

var imageInfo = new ImageCreateInfo
(
    ...
    format: targetFormat,
    usage: ImageUsageFlags.ColorAttachmentBit,
    pNext: &externalMemoryImageInfo
);

var importMemoryInfo = new ImportMemoryWin32HandleInfoKHR
(
    handleType: ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit,
    handle: renderTargetSharedHandle
);

vk.CreateImage(device, imageInfo, null, out directImage).Check();
```

> Note that D3D9 `X8R8G8B8` texture format map to Vulkan `B8G8R8A8Unorm`

### 5. Once the framebuffer is created, we are ready to interop

With WinUI we need to call Direct3D 11 to copy data from the render target to the back buffer and present it.

```csharp
// *rendering*
d3d11context.CopyResource(backbufferResource, renderTargetResource);
ThrowHResult(swapchain.Present(0u, (uint)SwapChainFlag.None));
```

With WPF we only need to set the D3D9 surface to the D3DImage, because the back buffer already contains the rendered image.

```csharp
d3dImage.Lock();
// *rendering*
d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, (nint)d3d9surface.Handle);
d3dImage.AddDirtyRect(new Int32Rect(0, 0, d3dImage.PixelWidth, d3dImage.PixelHeight));
d3dImage.Unlock();
```

# Prerequisites and build

* .NET 8
* [Windows SDK 10.0.22621](https://developer.microsoft.com/en-us/windows/downloads/windows-sdk/)

The example is `dotnet build` ready.
