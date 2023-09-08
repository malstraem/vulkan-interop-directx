using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.Storage;
using Windows.ApplicationModel;

using WinRT;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

using Interop.Vulkan;

namespace Interop.WinUI3;

[ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
partial interface ISwapChainPanelNative
{
    [PreserveSig]
    HResult SetSwapChain(ComPtr<IDXGISwapChain1> swapchain);
}

public sealed partial class MainWindow : Window
{
    private readonly Stopwatch stopwatch = new();

    private readonly VulkanInterop vulkanInterop = new();

    private readonly D3D11 d3d11 = D3D11.GetApi(null);

    private ComPtr<ID3D11Device> device;
    private ComPtr<ID3D11DeviceContext> context;

    private ComPtr<IDXGIAdapter> adapter;
    private ComPtr<IDXGIDevice3> dxgiDevice;
    private ComPtr<IDXGIFactory2> factory;

    private ComPtr<IDXGISwapChain1> swapchain;

    private ComPtr<ID3D11Texture2D> colorTexture;
    private ComPtr<ID3D11Texture2D> renderTargetTexture;

    private ComPtr<ID3D11Resource> colorResource;
    private ComPtr<ID3D11Resource> renderTargetResource;

    private nint sharedTextureHandle;

    private unsafe void InitializeDirectX()
    {
        #region Create device and context
        _ = d3d11.CreateDevice(
            default(ComPtr<IDXGIAdapter>),
            D3DDriverType.Hardware, 
            nint.Zero,
            (uint)CreateDeviceFlag.BgraSupport,
            null,
            0u,
            D3D11.SdkVersion,
            ref device,
            null,
            ref context);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Direct3D 11 device created: 0x{(nint)device.GetAddressOf():X16}");
        Console.WriteLine($"Direct3D 11 context created: 0x{(nint)context.GetAddressOf():X16}");
        #endregion

        #region Get DXGI device, adapter and factory
        dxgiDevice = device.QueryInterface<IDXGIDevice3>();

        _ = dxgiDevice.GetAdapter(ref adapter);

        factory = adapter.GetParent<IDXGIFactory2>();
        #endregion
    }

    private unsafe void CreateResources(uint width, uint height)
    {
        #region Create swapchain and get texture
        var swapchainDescription = new SwapChainDesc1
        {
            AlphaMode = AlphaMode.Unspecified,
            Format = Format.FormatB8G8R8A8Unorm,
            Width = width,
            Height = height,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            SampleDesc = new SampleDesc(1u, 0u),
            BufferUsage = DXGI.UsageRenderTargetOutput,
            BufferCount = 2u,
        };

        _ = factory.CreateSwapChainForComposition(dxgiDevice, swapchainDescription, default(ComPtr<IDXGIOutput>), ref swapchain);

        colorTexture = swapchain.GetBuffer<ID3D11Texture2D>(0u);
        #endregion

        #region Create render target texture
        var renderTargetDescription = new Texture2DDesc
        {
            Width = width,
            Height = height,
            Format = Format.FormatB8G8R8A8Unorm,
            BindFlags = (uint)BindFlag.RenderTarget,
            MiscFlags = (uint)ResourceMiscFlag.Shared,
            SampleDesc = new SampleDesc(1u, 0u),
            ArraySize = 1u,
            MipLevels = 1u
        };

        _ = device.CreateTexture2D(renderTargetDescription, null, ref renderTargetTexture);

        var resource = renderTargetTexture.QueryInterface<IDXGIResource>();

        void* sharedHandle;
        _ = resource.GetSharedHandle(&sharedHandle);

        sharedTextureHandle = (nint)sharedHandle;

        resource.Dispose();

        Console.WriteLine($"Shared texture created: 0x{sharedTextureHandle:X16}");
        #endregion

        colorResource = colorTexture.QueryInterface<ID3D11Resource>();
        renderTargetResource = renderTargetTexture.QueryInterface<ID3D11Resource>();
    }

    private void SetSwapchain() => swapchainPanel.As<ISwapChainPanelNative>().SetSwapChain(swapchain);

    private unsafe void OnRendering(object? sender, object e)
    {
        vulkanInterop.Draw(stopwatch.ElapsedMilliseconds / 1000f);
        context.CopyResource(colorResource, renderTargetResource);
        _ = swapchain.Present(0u, (uint)SwapChainFlag.None);
    }

    private void ReleaseResources()
    {
        colorResource.Dispose();
        renderTargetResource.Dispose();

        colorTexture.Dispose();
        renderTargetTexture.Dispose();

        swapchain.Dispose();
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

    private void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        Action action = ((ToggleSwitch)sender).IsOn ? stopwatch.Start : stopwatch.Stop;
        action();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
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

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBarRectangle);
    }
}
