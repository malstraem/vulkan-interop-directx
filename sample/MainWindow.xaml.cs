using System.Diagnostics;
using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using WinRT;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

using Interop.Vulkan;

namespace Interop.WinUI3
{
    public sealed partial class MainWindow : Window
    {
        private readonly Stopwatch stopwatch = new();

        private readonly VulkanInterop vulkanInterop = new();

        private readonly D3D11 d3d11 = D3D11.GetApi();

        private ComPtr<ID3D11Device> device;
        private ComPtr<ID3D11DeviceContext> context;

        private ComPtr<IDXGIAdapter> adapter;

        private ComPtr<IDXGIDevice3> dxgiDevice3;

        private ComPtr<IDXGIFactory2> factory2;

        private ComPtr<IDXGISwapChain1> swapchain1;

        private ComPtr<ID3D11Texture2D> colorTexture;
        private ComPtr<ID3D11Texture2D> renderTarget;

        private ComPtr<ID3D11Resource> colorResource;
        private ComPtr<ID3D11Resource> renderTargetResource;

        private nint sharedTextureHandle;

        private unsafe void InitializeDirectX()
        {
            #region Create device and context
            _ = d3d11.CreateDevice(
                null,
                D3DDriverType.Hardware, default,
                (uint)CreateDeviceFlag.BgraSupport,
                null,
                0u,
                D3D11.SdkVersion,
                device.GetAddressOf(),
                null,
                context.GetAddressOf());
            #endregion

            #region Get DXGI device and adapter
            var guid = IDXGIDevice3.Guid;

            _ = device.Get().QueryInterface(ref guid, (void**)dxgiDevice3.GetAddressOf());
            _ = dxgiDevice3.Get().GetAdapter(adapter.GetAddressOf());

            guid = IDXGIFactory2.Guid;
            _ = adapter.Get().GetParent(ref guid, (void**)factory2.GetAddressOf());
            #endregion
        }

        private unsafe void CreateResources(uint width, uint height)
        {
            #region Create swapchain
            var swapchainDesc1 = new SwapChainDesc1
            {
                AlphaMode = AlphaMode.Unspecified,
                Format = Format.FormatB8G8R8A8Unorm,
                BufferCount = 2u,
                Width = width,
                Height = height,
                SampleDesc = new SampleDesc(1u, 0u),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                BufferUsage = DXGI.UsageRenderTargetOutput,
            };

            _ = factory2.Get().CreateSwapChainForComposition((IUnknown*)dxgiDevice3.Handle, ref swapchainDesc1, null, swapchain1.GetAddressOf());
            #endregion

            var renderTargetDescription = new Texture2DDesc
            {
                CPUAccessFlags = (uint)CpuAccessFlag.None,
                Width = width,
                Height = height,
                Usage = Usage.Default,
                Format = Format.FormatB8G8R8A8Unorm,
                ArraySize = 1u,
                BindFlags = (uint)BindFlag.RenderTarget,
                MiscFlags = (uint)ResourceMiscFlag.Shared,
                MipLevels = 1u,
                SampleDesc = new SampleDesc(1u, 0u)
            };

            _ = device.Get().CreateTexture2D(ref renderTargetDescription, null, renderTarget.GetAddressOf());

            var guid = IDXGIResource.Guid;
            ComPtr<IDXGIResource> resource = default;

            _ = renderTarget.Get().QueryInterface(ref guid, (void**)resource.GetAddressOf());

            void* sharedHandle;
            _ = resource.Get().GetSharedHandle(&sharedHandle);

            sharedTextureHandle = (nint)sharedHandle;

            Console.WriteLine("Shared texture handle: 0x{0:X16}", sharedTextureHandle);

            guid = ID3D11Texture2D.Guid;

            _ = swapchain1.Get().GetBuffer(0, ref guid, (void**)colorTexture.GetAddressOf());

            guid = ID3D11Resource.Guid;

            _ = colorTexture.Get().QueryInterface(ref guid, (void**)colorResource.GetAddressOf());
            _ = renderTarget.Get().QueryInterface(ref guid, (void**)renderTargetResource.GetAddressOf());
        }

        private void OnSwapchainPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            uint width = (uint)e.NewSize.Width;
            uint height = (uint)e.NewSize.Height;

            Console.WriteLine($"SwapchainPanel resized: width - {width}, height - {height}");

            _ = swapchain1.Release();

            _ = colorTexture.Release();
            _ = renderTarget.Release();

            _ = colorResource.Release();
            _ = renderTargetResource.Release();

            CreateResources(width, height);

            SetSwapchain();

            vulkanInterop.Resize(sharedTextureHandle, width, height);
        }

        private void SetSwapchain()
        {
            var nativePanel = swapchainPanel.As<ISwapChainPanelNative>();
            _ = nativePanel.SetSwapChain(swapchain1);
        }

        private unsafe void Draw()
        {
            context.Get().CopyResource(colorResource.Handle, renderTargetResource.Handle);

            _ = swapchain1.Get().Present(0u, (uint)SwapChainFlag.None);
        }

        private void OnSwapchainPanelLoaded(object sender, RoutedEventArgs e)
        {
            uint width = (uint)swapchainPanel.ActualWidth;
            uint height = (uint)swapchainPanel.ActualHeight;

            InitializeDirectX();

            CreateResources(width, height);

            SetSwapchain();

            vulkanInterop.Initialize(sharedTextureHandle, width, height);

            swapchainPanel.SizeChanged += OnSwapchainPanelSizeChanged;

            CompositionTarget.Rendering += (s, e) =>
            {
                vulkanInterop.Draw(stopwatch.ElapsedMilliseconds / 1000f);
                Draw();
            };

            stopwatch.Start();
        }

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(titleBarRectangle);
        }

        [ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public partial interface ISwapChainPanelNative
        {
            [PreserveSig]
            HResult SetSwapChain(ComPtr<IDXGISwapChain1> swapchain);
        }
    }
}