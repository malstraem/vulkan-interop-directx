using System.Runtime.InteropServices;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using WinRT;

using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

using VulkanInterop;

namespace SwapchainApp.WinUI3
{
    public sealed partial class MainWindow : Window
    {
        private VulkanInteropApp vulkanApp = new();

        private nint sharedTextureHandle;

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

        private unsafe void InitializeDirectX()
        {
            #region Create device and context
            SilkMarshal.ThrowHResult
            (
                d3d11.CreateDevice(default(ComPtr<IDXGIAdapter>), D3DDriverType.Hardware, default, (uint)CreateDeviceFlag.BgraSupport, null, 0u, D3D11.SdkVersion, device.GetAddressOf(), null, context.GetAddressOf())
            );
            #endregion

            #region Get DXGI device and adapter
            var guid = IDXGIDevice3.Guid;

            device.Get().QueryInterface(ref guid, (void**)dxgiDevice3.GetAddressOf());
            dxgiDevice3.Get().GetAdapter(adapter.GetAddressOf());

            guid = IDXGIFactory2.Guid;
            adapter.Get().GetParent(ref guid, (void**)factory2.GetAddressOf());
            #endregion

            CreateSharedResources((uint)Bounds.Width, (uint)Bounds.Height);
        }

        private unsafe void CreateSharedResources(uint width, uint height)
        {
            #region Create swapchain
            var swapchainDesc1 = new SwapChainDesc1
            {
                AlphaMode = AlphaMode.Unspecified,
                Format = Format.FormatB8G8R8A8Unorm,
                BufferCount = 2,
                Width = width,
                Height = height,
                SampleDesc = new SampleDesc(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                BufferUsage = DXGI.UsageRenderTargetOutput,
            };

            factory2.Get().CreateSwapChainForComposition((IUnknown*)dxgiDevice3.Handle, ref swapchainDesc1, null, swapchain1.GetAddressOf());
            #endregion

            var renderTargetDescription = new Texture2DDesc
            {
                CPUAccessFlags = (uint)CpuAccessFlag.None,
                Width = width,
                Height = height,
                Usage = Usage.Default,
                Format = Format.FormatB8G8R8A8Unorm,
                ArraySize = 1,
                BindFlags = (uint)BindFlag.RenderTarget,
                MiscFlags = (uint)ResourceMiscFlag.Shared,
                MipLevels = 1,
                SampleDesc = new SampleDesc(1, 0)
            };

            device.Get().CreateTexture2D(ref renderTargetDescription, null, renderTarget.GetAddressOf());

            var guid = IDXGIResource.Guid;
            ComPtr<IDXGIResource> resource = default;

            renderTarget.Get().QueryInterface(ref guid, (void**)resource.GetAddressOf());

            void* sharedHandle;
            resource.Get().GetSharedHandle(&sharedHandle);

            sharedTextureHandle = (nint)sharedHandle;

            Console.WriteLine("Shared texture handle: 0x{0:X16}", sharedTextureHandle);

            guid = ID3D11Texture2D.Guid;

            swapchain1.Get().GetBuffer(0, ref guid, (void**)colorTexture.GetAddressOf());

            guid = ID3D11Resource.Guid;

            colorTexture.Get().QueryInterface(ref guid, (void**)colorResource.GetAddressOf());
            renderTarget.Get().QueryInterface(ref guid, (void**)renderTargetResource.GetAddressOf());

            var nativePanel = swapchainPanel.As<ISwapChainPanelNative>();
            _ = nativePanel.SetSwapChain(swapchain1);
        }

        private unsafe void Draw()
        {
            context.Get().CopyResource(colorResource.Handle, renderTargetResource.Handle);

            _ = swapchain1.Get().Present(0u, (uint)SwapChainFlag.None);
        }

        private void OnSwapchainPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            CompositionTarget.Rendering += (s, e) => Draw();

            var size = e.NewSize;

            Console.WriteLine($"SwapchainPanel resized: width - {size.Width}, height - {size.Height}");

            CreateSharedResources((uint)size.Width, (uint)size.Height);

            vulkanApp.Resize(sharedTextureHandle, (uint)size.Width, (uint)size.Height);

            var nativePanel = swapchainPanel.As<ISwapChainPanelNative>();
            _ = nativePanel.SetSwapChain(swapchain1);
        }

        public MainWindow()
        {
            InitializeComponent();

            InitializeDirectX();

            vulkanApp.Initialize(sharedTextureHandle, (uint)Bounds.Width, (uint)Bounds.Height);

            CompositionTarget.Rendering += (s, e) =>
            {
                vulkanApp.Draw();
                Draw();
            };
        }

        [ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public partial interface ISwapChainPanelNative
        {
            [PreserveSig]
            HResult SetSwapChain(ComPtr<IDXGISwapChain1> swapchain);
        }
    }
}