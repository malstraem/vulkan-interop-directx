using System.Diagnostics;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

using Interop.Vulkan;

using static Silk.NET.Core.Native.SilkMarshal;

#if WPF
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using Silk.NET.Direct3D9;

namespace Interop.WPF;
#elif WinUI
using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.Storage;
using Windows.ApplicationModel;

using WinRT;

namespace Interop.WinUI3;

[ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
partial interface ISwapChainPanelNative
{
    [PreserveSig]
    HResult SetSwapChain(ComPtr<IDXGISwapChain1> swapchain);
}
#endif

public sealed partial class MainWindow : Window
{
    private readonly Stopwatch stopwatch = new();

    private readonly VulkanInterop vulkanInterop = new();
#if WPF
    private readonly D3D9 d3d9 = D3D9.GetApi(null);

    private ComPtr<IDirect3D9Ex> d3d9Context;
    private ComPtr<IDirect3DDevice9Ex> d3d9device;

    private ComPtr<IDirect3DTexture9> backbufferTexture;

    private TimeSpan lastRenderTime;
#endif
    private readonly D3D11 d3d11 = D3D11.GetApi(null);

    private ComPtr<ID3D11Device> device;
    private ComPtr<ID3D11DeviceContext> context;

    private ComPtr<IDXGIAdapter> adapter;
    private ComPtr<IDXGIDevice3> dxgiDevice;
    private ComPtr<IDXGIFactory2> factory;

    private ComPtr<IDXGISwapChain1> swapchain;

    private ComPtr<ID3D11Texture2D> finalTexture;
    private ComPtr<ID3D11Texture2D> renderTargetTexture;

    private ComPtr<ID3D11Resource> finalTextureResource;
    private ComPtr<ID3D11Resource> renderTargetResource;

    private nint sharedTextureHandle;

    private unsafe void InitializeDirectX()
    {
        #region Create device and context
        ThrowHResult(d3d11.CreateDevice(
            default(ComPtr<IDXGIAdapter>),
            D3DDriverType.Hardware, 
            nint.Zero,
            (uint)CreateDeviceFlag.BgraSupport,
            null,
            0u,
            D3D11.SdkVersion,
            ref device,
            null,
            ref context));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Direct3D 11 device created: 0x{(nint)device.GetAddressOf():X16}");
        Console.WriteLine($"Direct3D 11 context created: 0x{(nint)context.GetAddressOf():X16}");
        #endregion

#if WinUI
        #region Get DXGI device, adapter and factory
        dxgiDevice = device.QueryInterface<IDXGIDevice3>();

        _ = dxgiDevice.GetAdapter(ref adapter);

        factory = adapter.GetParent<IDXGIFactory2>();
        #endregion
#elif WPF
        #region Create D3D9 context
        ThrowHResult(d3d9.Direct3DCreate9Ex(D3D9.SdkVersion, ref d3d9Context));

        var wih = new WindowInteropHelper(this);

        var presentParameters = new Silk.NET.Direct3D9.PresentParameters
        {
            BackBufferWidth = 1u,
            BackBufferHeight = 1u,
            BackBufferFormat  = Silk.NET.Direct3D9.Format.A8R8G8B8,
            SwapEffect = Swapeffect.Discard,
            Windowed = true
        };

        ThrowHResult(d3d9Context.CreateDeviceEx(0u, Devtype.Hal, wih.Handle, D3D9.CreateHardwareVertexprocessing, ref presentParameters, null, ref d3d9device));
        #endregion
#endif
    }

    private unsafe void CreateResources(uint width, uint height)
    {
        #region Create render target texture
        var renderTargetDescription = new Texture2DDesc
        {
            Width = width,
            Height = height,
            Format = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm,
            BindFlags = (uint)BindFlag.RenderTarget,
            MiscFlags = (uint)ResourceMiscFlag.Shared,
            SampleDesc = new SampleDesc(1u, 0u),
            ArraySize = 1u,
            MipLevels = 1u
        };

        ThrowHResult(device.CreateTexture2D(renderTargetDescription, null, ref renderTargetTexture));
        #endregion

        var dxgiResource = renderTargetTexture.QueryInterface<IDXGIResource>();

        void* sharedHandle;
        ThrowHResult(dxgiResource.GetSharedHandle(&sharedHandle));

        sharedTextureHandle = (nint)sharedHandle;

        dxgiResource.Dispose();

        Console.WriteLine($"Shared D3D11 texture created: 0x{sharedTextureHandle:X16}");
#if WinUI
        #region Create swapchain and get the texture
        var swapchainDescription = new SwapChainDesc1
        {
            Width = width,
            Height = height,
            Format = Format.FormatR8G8B8A8Unorm,
            SwapEffect = SwapEffect.FlipSequential,
            SampleDesc = new SampleDesc(1u, 0u),
            BufferUsage = DXGI.UsageBackBuffer,
            BufferCount = 2u,
        };

        ThrowHResult(factory.CreateSwapChainForComposition(dxgiDevice, swapchainDescription, default(ComPtr<IDXGIOutput>), ref swapchain));

        finalTexture = swapchain.GetBuffer<ID3D11Texture2D>(0u);
        #endregion
#elif WPF
        #region Create backbuffer texture
        var finalTextureDescription = new Texture2DDesc
        {
            Width = width,
            Height = height,
            ArraySize = 1u,
            MipLevels = 1u,
            Format = Silk.NET.DXGI.Format.FormatR8G8B8A8Unorm,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
            SampleDesc = new SampleDesc(1u, 0u)
        };

        _ = device.CreateTexture2D(finalTextureDescription, null, ref finalTexture);
        #endregion

        dxgiResource = finalTexture.QueryInterface<IDXGIResource>();
        ThrowHResult(dxgiResource.GetSharedHandle(&sharedHandle));
        dxgiResource.Dispose();

        void* backbufferShared = null;

        ThrowHResult(d3d9device.CreateTexture(
            width, height, 1u, D3D9.UsageRendertarget, Silk.NET.Direct3D9.Format.A8R8G8B8, Pool.Default, 
            ref backbufferTexture, ref backbufferShared));
#endif
        finalTextureResource = finalTexture.QueryInterface<ID3D11Resource>();
        renderTargetResource = renderTargetTexture.QueryInterface<ID3D11Resource>();
    }
#if WinUI
    private void SetSwapchain() => swapchainPanel.As<ISwapChainPanelNative>().SetSwapChain(swapchain);

    private void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        Action action = ((ToggleSwitch)sender).IsOn ? stopwatch.Start : stopwatch.Stop;
        action();
    }

    private unsafe void OnRendering(object? sender, object e)
    {
        vulkanInterop.Draw(stopwatch.ElapsedMilliseconds / 1000f);
        context.CopyResource(finalTextureResource, renderTargetResource);
        ThrowHResult(swapchain.Present(0u, (uint)SwapChainFlag.None));
    }

    private async void OnSwapchainPanelLoaded(object sender, RoutedEventArgs e)
    {
        uint width = (uint)swapchainPanel.ActualWidth;
        uint height = (uint)swapchainPanel.ActualHeight;

        InitializeDirectX();

        CreateResources(width, height);

        SetSwapchain();

        var folder = await StorageFolder.GetFolderFromPathAsync(Package.Current.InstalledPath);
        var assetfolder = await folder.GetFolderAsync("assets");
        var helmetFile = await assetfolder.GetFileAsync("DamagedHelmet.glb");

        using var stream = await helmetFile.OpenStreamForReadAsync();

        vulkanInterop.Initialize(sharedTextureHandle, width, height, stream);

        swapchainPanel.SizeChanged += OnSwapchainPanelSizeChanged;

        CompositionTarget.Rendering += OnRendering;
    }

    private void OnSwapchainPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        uint width = (uint)e.NewSize.Width;
        uint height = (uint)e.NewSize.Height;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"SwapchainPanel resized: width - {width}, height - {height}");

        ReleaseResources();

        CreateResources(width, height);

        SetSwapchain();

        vulkanInterop.Resize(sharedTextureHandle, width, height);
    }
#elif WPF
    private unsafe void OnRendering(object? sender, EventArgs e)
    {
        RenderingEventArgs args = (RenderingEventArgs)e;

        if (dxImage.IsFrontBufferAvailable && lastRenderTime != args.RenderingTime)
        {
            ComPtr<IDirect3DSurface9> surface = null;

            ThrowHResult(backbufferTexture.GetSurfaceLevel(0u, ref surface));

            vulkanInterop.Draw(stopwatch.ElapsedMilliseconds / 1000f);

            context.CopyResource(finalTextureResource, renderTargetResource);
            context.Flush();

            dxImage.Lock();
            dxImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, (nint)surface.GetPinnableReference());

            dxImage.AddDirtyRect(new Int32Rect(0, 0, dxImage.PixelWidth, dxImage.PixelHeight));
            dxImage.Unlock();

            lastRenderTime = args.RenderingTime;
        }
    }

    private void OnFrameImageLoaded(object sender, RoutedEventArgs e)
    {
        uint width = (uint)Width;
        uint height = (uint)Height;

        InitializeDirectX();

        CreateResources(width, height);

        vulkanInterop.Initialize(sharedTextureHandle, width, height, File.Open("assets/DamagedHelmet.glb", FileMode.Open));

        CompositionTarget.Rendering += OnRendering;
    }
#endif
    private void ReleaseResources()
    {
        finalTextureResource.Dispose();
        renderTargetResource.Dispose();

        finalTexture.Dispose();
        renderTargetTexture.Dispose();

        swapchain.Dispose();
    }

    private void OnWindowClosed(object sender,
#if WinUI 
        WindowEventArgs
#elif WPF
        EventArgs
#endif
        args)
    {
        CompositionTarget.Rendering -= OnRendering;

        ReleaseResources();

        factory.Dispose();
        adapter.Dispose();
        dxgiDevice.Dispose();
        context.Dispose();
        device.Dispose();

        vulkanInterop.Clear();
    }

    public MainWindow()
    {
        InitializeComponent();
#if WinUI
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBarRectangle);
#endif
    }
}
