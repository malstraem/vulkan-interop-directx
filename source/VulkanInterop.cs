using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Interop.Vulkan;

public static class ResultExtensions
{
    [DebuggerHidden]
    public static void Check(this Result result)
    {
        if (result < 0)
            throw new Exception($"Vulkan calling failed - {result}");
    }
}

public struct KeyedMutexSyncInfo
{
    public ulong AcquireKey;
    public ulong ReleaseKey;
    public uint Timeout; // In milliseconds
}

public unsafe class VulkanInterop
{
    private const int VK_LUID_SIZE = 8;
    private const string interopExtensionName = "VK_KHR_external_memory_win32";

    private readonly struct VertexPositionColor(Vector3 position, Vector3 color)
    {
        public readonly Vector3 Position = position;
        public readonly Vector3 Color = color;

        public static VertexInputBindingDescription GetBindingDescription() => new()
        {
            Stride = (uint)sizeof(VertexPositionColor),
            InputRate = VertexInputRate.Vertex
        };

        public static VertexInputAttributeDescription[] GetAttributeDescriptions() =>
        [
            new VertexInputAttributeDescription()
            {
                Format = Format.R32G32B32Sfloat,
                Offset = (uint)Marshal.OffsetOf<VertexPositionColor>(nameof(Position)),
            },
            new VertexInputAttributeDescription()
            {
                Format = Format.R32G32B32Sfloat,
                Offset = (uint)Marshal.OffsetOf<VertexPositionColor>(nameof(Color)),
                Location = 1u
            }
        ];
    }

    private struct ModelViewProjection
    {
        public Matrix4x4 Model;
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

#pragma warning disable CS8618
    private uint[] indices;

    private VertexPositionColor[] vertices;
#pragma warning restore CS8618

    private ModelViewProjection modelViewProjection;

    private uint width;
    private uint height;

    private readonly Vk vk = Vk.GetApi();

    private Instance instance;

    private Device device;
    private PhysicalDevice physicalDevice;
    private PhysicalDeviceProperties physicalDeviceProperties;

    private Queue queue;

    private Fence fence;

    private Image colorImage, depthImage, directImage;
    private ImageView colorView, depthView, directView;

    private Framebuffer framebuffer;

    private Pipeline pipeline;
    private PipelineLayout pipelineLayout;

    private DescriptorPool descriptorPool;
    private DescriptorSetLayout descriptorSetLayout;
    private DescriptorSet descriptorSet;

    private RenderPass renderPass;

    private CommandPool commandPool;
    private CommandBuffer commandBuffer;

    private Buffer vertexBuffer, indexBuffer, uniformBuffer;

    private DeviceMemory vertexMemory, indexMemory, uniformMemory,
                         colorImageMemory, depthImageMemory, directImageMemory;

    private ShaderModule vertexShaderModule, fragmentShaderModule;

    private Format depthFormat;
    private Format targetFormat;
    private ExternalMemoryHandleTypeFlags targetHandleType;
    private SampleCountFlags sampleCount = SampleCountFlags.Count8Bit;

    private static byte[] ReadBytes(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetName().Name!;

        using var stream = assembly.GetManifestResourceStream($"{name}.{filename}");
        using var reader = new BinaryReader(stream!);

        return reader.ReadBytes((int)stream!.Length);
    }

    private static Luid RtlConvertUlongToLuid(ulong val)
    {
        // Should behave the same as Window's RtlConvertUlongToLuid in ntddk.h
        return new Luid
        {
            Low = (uint)val,
            High = 0
        };
    }

    private Luid VulkanDeviceLuidToLuid(byte* vulkanDeviceLuidPtr)
    {
        var vulkanLuidBytes = new byte[VK_LUID_SIZE];
        for (int i = 0; i < VK_LUID_SIZE; i++)
        {
            vulkanLuidBytes[i] = vulkanDeviceLuidPtr[i];
        }

        ulong vulkanLuidUlong = BitConverter.ToUInt64(vulkanLuidBytes);
        return RtlConvertUlongToLuid(vulkanLuidUlong);
    }

    private Format FindSupportedFormat(Format[] candidates, ImageTiling tiling, FormatFeatureFlags features)
    {
        foreach (var candidate in candidates)
        {
            vk.GetPhysicalDeviceFormatProperties(physicalDevice, candidate, out var properties);

            if ((tiling == ImageTiling.Linear && (properties.LinearTilingFeatures & features) == features)
                 || (tiling == ImageTiling.Optimal && (properties.OptimalTilingFeatures & features) == features))
            {
                return candidate;
            }
        }
        throw new Exception("Supported format not found");
    }

    private bool CheckGraphicsQueue(PhysicalDevice physicalDevice, ref uint index)
    {
        uint queueFamilyCount = 0u;
        vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];

        fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilyCount, queueFamiliesPtr);

        foreach (var queueFamily in queueFamilies)
        {
            if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                return true;

            index++;
        }
        return false;
    }

    private bool CheckExternalMemoryExtension(PhysicalDevice physicalDevice) 
    {
        uint propertyCount = 0u;
        byte layerName = default;
        vk.EnumerateDeviceExtensionProperties(physicalDevice, layerName, ref propertyCount, null).Check();

        var extensionProperties = new Span<ExtensionProperties>(new ExtensionProperties[propertyCount]);
        vk.EnumerateDeviceExtensionProperties(physicalDevice, &layerName, &propertyCount, extensionProperties).Check();

        foreach (var extensionProperty in extensionProperties)
        {
            if (Encoding.UTF8.GetString(extensionProperty.ExtensionName, 256).Trim('\0') == interopExtensionName)
                return true;
        }

        return false;
    }

    private bool CheckPhysicalDeviceLuid(byte* vulkanDeviceLuidPtr, Luid targetDeviceLuid, ExternalMemoryHandleTypeFlags targetHandleType)
    {
        // Some external memory handle types can only be shared within the same underlying physical
        // device and/or the same driver version. Best we can do with D3D is check the LUID.
        switch (targetHandleType)
        {
            case ExternalMemoryHandleTypeFlags.OpaqueFDBit:
            case ExternalMemoryHandleTypeFlags.OpaqueWin32Bit:
            case ExternalMemoryHandleTypeFlags.OpaqueWin32KmtBit:
            case ExternalMemoryHandleTypeFlags.D3D11TextureBit:
            case ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit:
            case ExternalMemoryHandleTypeFlags.D3D12HeapBit:
            case ExternalMemoryHandleTypeFlags.D3D12ResourceBit:
                // Same underlying physical device required, check LUID
                var vulkanLuid = VulkanDeviceLuidToLuid(vulkanDeviceLuidPtr);
                return vulkanLuid.Equals(targetDeviceLuid);
            default:
                // Same underlying physical device not required
                return true;
        }
    }

    private bool CheckExternalImageHandleType(PhysicalDevice physicalDevice, Format targetFormat, ExternalMemoryHandleTypeFlags targetHandleType)
    {
        var externalFormatInfo = new PhysicalDeviceExternalImageFormatInfo
        (
            handleType: targetHandleType
        );

        var formatInfo = new PhysicalDeviceImageFormatInfo2
        (
            pNext: &externalFormatInfo,
            format: targetFormat,
            type: ImageType.Type2D,
            tiling: ImageTiling.Optimal,
            usage: ImageUsageFlags.ColorAttachmentBit
        );

        var externalFormatProperties = new ExternalImageFormatProperties(StructureType.ExternalImageFormatProperties);
        var formatProperties = new ImageFormatProperties2(pNext: &externalFormatProperties);

        var result = vk.GetPhysicalDeviceImageFormatProperties2(physicalDevice, in formatInfo, &formatProperties);

        if (result == Result.ErrorFormatNotSupported)
        {
            // VK_ERROR_FORMAT_NOT_SUPPORTED can be returned when the handle type is not supported by the device
            return false;
        }

        if (result != Result.Success)
        {
            throw new Exception($"External handle type check failed - {result}");
        }

        return externalFormatProperties.ExternalMemoryProperties.ExternalMemoryFeatures.HasFlag(ExternalMemoryFeatureFlags.ImportableBit);
    }

    private uint GetMemoryTypeIndex(uint typeBits, MemoryPropertyFlags memoryPropertyFlags)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memoryProperties);

        for (int i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            if ((typeBits & (1 << i)) != 0 && (memoryProperties.MemoryTypes[i].PropertyFlags & memoryPropertyFlags) == memoryPropertyFlags)
                return (uint)i;
        }

        throw new Exception("Memory type not found");
    }

    private ExternalMemoryFeatureFlags GetImageFormatExternalMemoryFeatures(ImageCreateInfo imageInfo, ExternalMemoryHandleTypeFlags handleType)
    {
        var externalFormatInfo = new PhysicalDeviceExternalImageFormatInfo
        (
            handleType: handleType
        );

        var formatInfo = new PhysicalDeviceImageFormatInfo2
        (
            pNext: &externalFormatInfo,
            format: imageInfo.Format,
            usage: imageInfo.Usage,
            type: imageInfo.ImageType,
            tiling: imageInfo.Tiling
        );

        var externalFormatProperties = new ExternalImageFormatProperties(StructureType.ExternalImageFormatProperties);
        var formatProperties = new ImageFormatProperties2(pNext: &externalFormatProperties);

        vk.GetPhysicalDeviceImageFormatProperties2(physicalDevice, in formatInfo, &formatProperties).Check();

        return externalFormatProperties.ExternalMemoryProperties.ExternalMemoryFeatures;
    }

    private unsafe ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* codePtr = code)
        {
            var createInfo = new ShaderModuleCreateInfo(codeSize: (nuint)code.Length, pCode: (uint*)codePtr);

            vk.CreateShaderModule(device, createInfo, null, out var shaderModule).Check();

            return shaderModule;
        }
    }

    private void CreateBuffer(ulong size, BufferUsageFlags bufferUsage, MemoryPropertyFlags memoryProperties, out Buffer buffer, out DeviceMemory deviceMemory)
    {
        var bufferCreateInfo = new BufferCreateInfo(sharingMode: SharingMode.Exclusive, usage: bufferUsage, size: size);

        vk.CreateBuffer(device, bufferCreateInfo, null, out buffer).Check();
        vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);

        var memoryAllocateInfo = new MemoryAllocateInfo
        (
            allocationSize: memoryRequirements.Size, 
            memoryTypeIndex: GetMemoryTypeIndex(memoryRequirements.MemoryTypeBits, memoryProperties)
        );

        vk.AllocateMemory(device, memoryAllocateInfo, null, out deviceMemory).Check();
        vk.BindBufferMemory(device, buffer, deviceMemory, 0ul).Check();
    }

    private (Image Image, ImageView ImageView, DeviceMemory ImageMemory) CreateImageView(Format imageFormat, SampleCountFlags sampleCount = SampleCountFlags.Count1Bit,
        ImageUsageFlags imageUsage = ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags viewAspect = ImageAspectFlags.ColorBit)
    {
        var imageInfo = new ImageCreateInfo
        (
            imageType: ImageType.Type2D,
            format: imageFormat,
            samples: sampleCount,
            usage: imageUsage,
            mipLevels: 1u,
            arrayLayers: 1u,
            extent: new Extent3D(width: width, height: height, depth: 1u)
        );

        vk.CreateImage(device, imageInfo, null, out var image).Check();

        vk.GetImageMemoryRequirements(device, image, out var memoryRequirements);

        var memoryInfo = new MemoryAllocateInfo
        (
            allocationSize: memoryRequirements.Size,
            memoryTypeIndex: GetMemoryTypeIndex(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        );

        vk.AllocateMemory(device, memoryInfo, null, out var imageMemory).Check();
        vk.BindImageMemory(device, image, imageMemory, 0ul).Check();

        var imageViewInfo = new ImageViewCreateInfo
        (
            image: image,
            viewType: ImageViewType.Type2D,
            format: imageFormat,
            subresourceRange: new ImageSubresourceRange
            {
                AspectMask = viewAspect,
                LevelCount = 1u,
                LayerCount = 1u
            }
        );

        vk.CreateImageView(device, imageViewInfo, null, out var view).Check();

        return (image, view, imageMemory);
    }

    public void CreateImageViews(nint directTextureHandle)
    {
        (colorImage, colorView, colorImageMemory) = CreateImageView(targetFormat, sampleCount);
        (depthImage, depthView, depthImageMemory) = CreateImageView(depthFormat, sampleCount, ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);

        #region Especial create image and view using handle and external memory of DirectX texture
        var externalMemoryImageInfo = new ExternalMemoryImageCreateInfo
        (
            handleTypes: targetHandleType
        );

        var imageInfo = new ImageCreateInfo
        (
            pNext: &externalMemoryImageInfo,
            usage: ImageUsageFlags.ColorAttachmentBit,
            format: targetFormat,
            imageType: ImageType.Type2D,
            mipLevels: 1u,
            arrayLayers: 1u,
            samples: SampleCountFlags.Count1Bit,
            extent: new Extent3D(width: width, height: height, depth: 1u)
        );

        vk.CreateImage(device, imageInfo, null, out directImage).Check();

        var requirements = vk.GetImageMemoryRequirements(device, directImage);

        var importMemoryInfo = new ImportMemoryWin32HandleInfoKHR
        (
            handleType: targetHandleType,
            handle: directTextureHandle
        );

        var memoryInfo = new MemoryAllocateInfo
        (
            pNext: &importMemoryInfo,
            allocationSize: requirements.Size,
            memoryTypeIndex: GetMemoryTypeIndex(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        );

        var dedicatedAllocateInfo = new MemoryDedicatedAllocateInfo(image: directImage);
        var externalMemoryFeatures = GetImageFormatExternalMemoryFeatures(imageInfo, targetHandleType);
        if (externalMemoryFeatures.HasFlag(ExternalMemoryFeatureFlags.DedicatedOnlyBit))
        {
            importMemoryInfo.PNext = &dedicatedAllocateInfo;
        }

        vk.AllocateMemory(device, memoryInfo, null, out directImageMemory).Check();
        vk.BindImageMemory(device, directImage, directImageMemory, 0ul).Check();

        var imageViewInfo = new ImageViewCreateInfo
        (
            image: directImage,
            format: targetFormat,
            viewType: ImageViewType.Type2D,
            subresourceRange: new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1u,
                LayerCount = 1u
            }
        );

        vk.CreateImageView(device, imageViewInfo, null, out directView).Check();

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Imported Vulkan image: 0x{directImage.Handle:X8}");
        #endregion
    }

    private void CreatePipeline()
    {
        var vertexShaderStage = new PipelineShaderStageCreateInfo
        (
            module: vertexShaderModule,
            stage: ShaderStageFlags.VertexBit,
            pName: (byte*)SilkMarshal.StringToPtr("main")
        );

        var fragmentShaderStage = new PipelineShaderStageCreateInfo
        (
            module: fragmentShaderModule,
            stage: ShaderStageFlags.FragmentBit,
            pName: (byte*)SilkMarshal.StringToPtr("main")
        );

        var shaderStages = stackalloc[] { vertexShaderStage, fragmentShaderStage };

        var bindingDescription = VertexPositionColor.GetBindingDescription();
        var attributeDescriptions = VertexPositionColor.GetAttributeDescriptions();

        fixed (VertexInputAttributeDescription* attributeDescriptionsPtr = attributeDescriptions)
        {
            var vertexInputState = new PipelineVertexInputStateCreateInfo
            (
                vertexBindingDescriptionCount: 1u,
                pVertexBindingDescriptions: &bindingDescription,
                vertexAttributeDescriptionCount: (uint)attributeDescriptions.Length,
                pVertexAttributeDescriptions: attributeDescriptionsPtr
            );

            var inputAssemblyState = new PipelineInputAssemblyStateCreateInfo(topology: PrimitiveTopology.TriangleList);

            var viewport = new Viewport
            {
                Width = width,
                Height = height,
                MaxDepth = 1f
            };

            var scissorRect = new Rect2D
            {
                Extent = new(width, height) 
            };

            var viewportState = new PipelineViewportStateCreateInfo
            (
                viewportCount: 1u,
                pViewports: &viewport,
                scissorCount: 1u,
                pScissors: &scissorRect
            );

            var rasterizationState = new PipelineRasterizationStateCreateInfo
            (
                cullMode: CullModeFlags.BackBit,
                frontFace: FrontFace.Clockwise,
                polygonMode: PolygonMode.Fill,
                lineWidth: 1
            );

            var multisampleState = new PipelineMultisampleStateCreateInfo(rasterizationSamples: sampleCount);

            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit 
                               | ColorComponentFlags.GBit 
                               | ColorComponentFlags.BBit 
                               | ColorComponentFlags.ABit
            };

            var colorBlendState = new PipelineColorBlendStateCreateInfo
            (
                logicOp: LogicOp.Copy,
                attachmentCount: 1u,
                pAttachments: &colorBlendAttachment
            );

            var depthStencilState = new PipelineDepthStencilStateCreateInfo
            (
                depthTestEnable: true,
                depthWriteEnable: true,
                depthCompareOp: CompareOp.Less,
                maxDepthBounds: 1u
            );

            var pipelineCreateInfo = new GraphicsPipelineCreateInfo
            (
                layout: pipelineLayout,
                renderPass: renderPass,
                stageCount: 2,
                pStages: shaderStages,
                pVertexInputState: &vertexInputState,
                pInputAssemblyState: &inputAssemblyState,
                pViewportState: &viewportState,
                pRasterizationState: &rasterizationState,
                pMultisampleState: &multisampleState,
                pColorBlendState: &colorBlendState,
                pDepthStencilState: &depthStencilState
            );

            vk.CreateGraphicsPipelines(device, default, 1u, pipelineCreateInfo, null, out pipeline).Check();
        }

        _ = SilkMarshal.Free((nint)vertexShaderStage.PName);
        _ = SilkMarshal.Free((nint)fragmentShaderStage.PName);
    }

    private void CreateFramebuffer()
    {
        var attachments = stackalloc ImageView[3] { colorView, depthView, directView };

        var framebufferCreateInfo = new FramebufferCreateInfo
        (
            renderPass: renderPass,
            width: width,
            height: height,
            layers: 1u,
            attachmentCount: 3u,
            pAttachments: attachments
        );

        vk.CreateFramebuffer(device, framebufferCreateInfo, null, out framebuffer).Check();
    }

    private void CreateCommandBuffer()
    {
        var commandBufferAllocateInfo = new CommandBufferAllocateInfo(level: CommandBufferLevel.Primary, commandPool: commandPool, commandBufferCount: 1u);

        vk.AllocateCommandBuffers(device, commandBufferAllocateInfo, out commandBuffer).Check();

        vk.BeginCommandBuffer(commandBuffer, new CommandBufferBeginInfo(sType: StructureType.CommandBufferBeginInfo)).Check();

        var clearValues = stackalloc ClearValue[3]
        {
            new(color: new(0f, 0f, 0f, 1f)),
            new(depthStencil: new(1f, 0u)),
            new(color: new(0f, 0f, 0f, 1f))
        };

        var renderPassBeginInfo = new RenderPassBeginInfo
        (
            renderPass: renderPass,
            framebuffer: framebuffer,
            clearValueCount: 3u,
            pClearValues: clearValues,
            renderArea: new(offset: new(0, 0), extent: new(width, height))
        );

        vk.CmdBeginRenderPass(commandBuffer, renderPassBeginInfo, SubpassContents.Inline);

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindVertexBuffers(commandBuffer, 0u, 1u, vertexBuffer, 0ul);
        vk.CmdBindIndexBuffer(commandBuffer, indexBuffer, 0ul, IndexType.Uint32);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 0u, 1u, descriptorSet, 0u, null);

        vk.CmdDrawIndexed(commandBuffer, (uint)indices.Length, 1u, 0u, 0, 0u);

        vk.CmdEndRenderPass(commandBuffer);

        vk.EndCommandBuffer(commandBuffer).Check();
    }

    public void Initialize(nint directTextureHandle, Luid targetDeviceLuid, uint width, uint height, Format targetFormat, ExternalMemoryHandleTypeFlags targetHandleType, Stream modelStream)
    {
        this.width = width;
        this.height = height;

        this.targetFormat = targetFormat;
        this.targetHandleType = targetHandleType;

        #region Create instance
        var appInfo = new ApplicationInfo(apiVersion: Vk.Version11);
        var createInfo = new InstanceCreateInfo(pApplicationInfo: &appInfo);

        vk.CreateInstance(createInfo, null, out instance).Check();

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Vulkan instance: 0x{instance.Handle:X8}");
        #endregion

        uint queueIndex = 0u;

        #region Pick physical device
        foreach (var physicalDevice in vk.GetPhysicalDevices(instance))
        {
            uint propertyCount = 0u;
            byte layerName = default;
            vk.EnumerateDeviceExtensionProperties(physicalDevice, layerName, ref propertyCount, null).Check();

            var extensionProperties = new Span<ExtensionProperties>(new ExtensionProperties[propertyCount]);
            vk.EnumerateDeviceExtensionProperties(physicalDevice, &layerName, &propertyCount, extensionProperties).Check();

            var idProperties = new PhysicalDeviceIDProperties(sType: StructureType.PhysicalDeviceIDProperties);
            var properties2 = new PhysicalDeviceProperties2(pNext: &idProperties);
            vk.GetPhysicalDeviceProperties2(physicalDevice, &properties2);

            if (CheckGraphicsQueue(physicalDevice, ref queueIndex)
                && CheckExternalMemoryExtension(physicalDevice)
                && CheckExternalImageHandleType(physicalDevice, targetFormat, targetHandleType)
                && CheckPhysicalDeviceLuid(idProperties.DeviceLuid, targetDeviceLuid, targetHandleType))
            {
                this.physicalDevice = physicalDevice;
                this.physicalDeviceProperties = properties2.Properties;
                break;
            }
        }

        if (physicalDevice.Handle == nint.Zero)
            throw new Exception("Suitable device not found");

        depthFormat = FindSupportedFormat([Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint], ImageTiling.Optimal, FormatFeatureFlags.DepthStencilAttachmentBit);

        var sampleCounts = physicalDeviceProperties.Limits.FramebufferDepthSampleCounts & physicalDeviceProperties.Limits.FramebufferColorSampleCounts;

        sampleCount = sampleCounts switch
        {
            _ when sampleCounts.HasFlag(SampleCountFlags.Count8Bit) => SampleCountFlags.Count8Bit,
            _ when sampleCounts.HasFlag(SampleCountFlags.Count4Bit) => SampleCountFlags.Count4Bit,
            _ when sampleCounts.HasFlag(SampleCountFlags.Count2Bit) => SampleCountFlags.Count2Bit,
            _ => SampleCountFlags.Count1Bit
        };

        fixed (byte* deviceName = physicalDeviceProperties.DeviceName)
        {
            Console.WriteLine($"{Encoding.UTF8.GetString(deviceName, 256).Trim('\0')} having {interopExtensionName} extension: 0x{physicalDevice.Handle:X8}");
        }
        #endregion

        #region Create device
        float queuePriority = 1f;

        var deviceQueueCreateInfo = new DeviceQueueCreateInfo
        (
            queueFamilyIndex: queueIndex,
            pQueuePriorities: &queuePriority,
            queueCount: 1u
        );

        string[] extensions = [interopExtensionName];

        var deviceCreateInfo = new DeviceCreateInfo
        (
            pQueueCreateInfos: &deviceQueueCreateInfo,
            queueCreateInfoCount: 1u,
            ppEnabledExtensionNames: (byte**)SilkMarshal.StringArrayToPtr(extensions),
            enabledExtensionCount: 1u
        );

        vk.CreateDevice(physicalDevice, deviceCreateInfo, null, out device).Check();

        Console.WriteLine($"Vulkan device: 0x{device.Handle:X8}");
        #endregion

        vk.GetDeviceQueue(device, queueIndex, 0u, out queue);

        vk.CreateFence(device, new FenceCreateInfo(sType: StructureType.FenceCreateInfo), null, out fence).Check();

        vk.CreateCommandPool(device, new CommandPoolCreateInfo(queueFamilyIndex: queueIndex), null, out commandPool).Check();

        #region Create shader modules
        byte[] vertShaderCode = ReadBytes("shaders.shader.vert.spv");
        byte[] fragShaderCode = ReadBytes("shaders.shader.frag.spv");

        vertexShaderModule = CreateShaderModule(vertShaderCode);
        fragmentShaderModule = CreateShaderModule(fragShaderCode);
        #endregion

        #region Create render pass
        var colorAttachmentDescription = new AttachmentDescription
        {
            Format = targetFormat,
            Samples = sampleCount,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
            //InitialLayout = ImageLayout.ColorAttachmentOptimal
        };

        var colorAttachmentResolveDescription = new AttachmentDescription
        {
            Format = targetFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
            //InitialLayout = ImageLayout.ColorAttachmentOptimal
        };

        var depthAttachmentDescription = new AttachmentDescription
        {
            Format = depthFormat,
            Samples = sampleCount,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            //InitialLayout = ImageLayout.DepthStencilAttachmentOptimal
        };

        var colorAttachmentReference = new AttachmentReference(0u, ImageLayout.ColorAttachmentOptimal);
        var depthAttachmentReference = new AttachmentReference(1u, ImageLayout.DepthStencilAttachmentOptimal);
        var colorAttachmentResolveReference = new AttachmentReference(2u, ImageLayout.ColorAttachmentOptimal);

        var subpassDescription = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1u,
            PColorAttachments = &colorAttachmentReference,
            PResolveAttachments = &colorAttachmentResolveReference,
            PDepthStencilAttachment = &depthAttachmentReference
        };

        var subpassDependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
        };

        var attachments = stackalloc AttachmentDescription[3] { colorAttachmentDescription, depthAttachmentDescription, colorAttachmentResolveDescription };

        var renderPassCreateInfo = new RenderPassCreateInfo
        (
            attachmentCount: 3,
            pAttachments: attachments,
            subpassCount: 1,
            pSubpasses: &subpassDescription,
            dependencyCount: 1,
            pDependencies: &subpassDependency
        );

        vk.CreateRenderPass(device, renderPassCreateInfo, null, out renderPass).Check();
        #endregion

        #region Create vertex, index and uniform buffers
        var model = SharpGLTF.Schema2.ModelRoot.ReadGLB(modelStream);

        var modelIndices = new List<uint>();
        var modelVertices = new List<VertexPositionColor>();

        foreach (var mesh in model.LogicalMeshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                modelIndices.AddRange(primitive.GetIndices());

                var positions = primitive.VertexAccessors["POSITION"].AsVector3Array();
                var normals = primitive.VertexAccessors["NORMAL"].AsVector3Array();

                for (int i = 0; i < positions.Count; i++)
                {
                    var position = positions[i];
                    var normal = normals[i];

                    modelVertices.Add(new VertexPositionColor(position, normal));
                }
            }
        }

        indices = [.. modelIndices];
        vertices = [.. modelVertices];

        ulong indexBufferSize = (ulong)(indices.Length * sizeof(uint));
        ulong vertexBufferSize = (ulong)(vertices.Length * sizeof(VertexPositionColor));

        ulong mvpBufferSize = (ulong)sizeof(ModelViewProjection);

        void* data;

        CreateBuffer(indexBufferSize, BufferUsageFlags.IndexBufferBit, MemoryPropertyFlags.HostVisibleBit, out indexBuffer, out indexMemory);

        vk.MapMemory(device, indexMemory, 0ul, indexBufferSize, 0u, &data).Check();
        indices.AsSpan().CopyTo(new Span<uint>(data, indices.Length));
        vk.UnmapMemory(device, indexMemory);

        CreateBuffer(vertexBufferSize, BufferUsageFlags.VertexBufferBit, MemoryPropertyFlags.HostVisibleBit, out vertexBuffer, out vertexMemory);

        vk.MapMemory(device, vertexMemory, 0ul, vertexBufferSize, 0u, &data).Check();
        vertices.AsSpan().CopyTo(new Span<VertexPositionColor>(data, vertices.Length));
        vk.UnmapMemory(device, vertexMemory);

        CreateBuffer(mvpBufferSize, BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit, out uniformBuffer, out uniformMemory);
        #endregion

        #region Create descriptor pool
        var descriptorPoolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = 1u
        };

        var descriptorPoolCreateInfo = new DescriptorPoolCreateInfo(pPoolSizes: &descriptorPoolSize, poolSizeCount: 1u, maxSets: 1u);

        vk.CreateDescriptorPool(device, descriptorPoolCreateInfo, null, out descriptorPool).Check();
        #endregion

        #region Create descriptor set layout
        var descriptorSetLayoutBinding = new DescriptorSetLayoutBinding
        {
            DescriptorType = DescriptorType.UniformBuffer,
            StageFlags = ShaderStageFlags.VertexBit,
            DescriptorCount = 1u
        };

        var descriptorSetLayoutCreateInfo = new DescriptorSetLayoutCreateInfo(pBindings: &descriptorSetLayoutBinding, bindingCount: 1u);

        vk.CreateDescriptorSetLayout(device, descriptorSetLayoutCreateInfo, null, out descriptorSetLayout).Check();
        #endregion

        #region Create descriptor set
        fixed (DescriptorSetLayout* setLayoutPtr = &descriptorSetLayout)
        {
            var descriptorSetAllocateInfo = new DescriptorSetAllocateInfo(descriptorPool: descriptorPool, pSetLayouts: setLayoutPtr, descriptorSetCount: 1u);

            vk.AllocateDescriptorSets(device, descriptorSetAllocateInfo, out descriptorSet).Check();
        }

        var descriptorBufferInfo = new DescriptorBufferInfo
        {
            Buffer = uniformBuffer,
            Range = (ulong)sizeof(ModelViewProjection)
        };

        var writeDescriptorSet = new WriteDescriptorSet
        (
            dstSet: descriptorSet,
            descriptorType: DescriptorType.UniformBuffer,
            descriptorCount: 1u,
            pBufferInfo: &descriptorBufferInfo
        );

        vk.UpdateDescriptorSets(device, 1u, writeDescriptorSet, 0u, null);
        #endregion

        fixed (DescriptorSetLayout* descriptorSetLayoutPtr = &descriptorSetLayout)
        {
            var layoutCreateInfo = new PipelineLayoutCreateInfo(setLayoutCount: 1u, pSetLayouts: descriptorSetLayoutPtr);

            vk.CreatePipelineLayout(device, layoutCreateInfo, null, out pipelineLayout).Check();
        }

        CreateImageViews(directTextureHandle);
        CreatePipeline();
        CreateFramebuffer();
        CreateCommandBuffer();

        modelViewProjection = new ModelViewProjection()
        {
            Model = Matrix4x4.Identity,
            View = Matrix4x4.CreateLookAt(new Vector3(5f, 0f, 0f), Vector3.Zero, Vector3.UnitZ),
            Projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 180f * 45f, (float)width / height, 0.001f, 10000f)
        };
    }

    private void UpdateModelViewProjection(float time)
    {
        modelViewProjection.Model = Matrix4x4.CreateRotationZ(time * RotationSpeed);

        void* data;

        vk.MapMemory(device, uniformMemory, 0ul, (ulong)sizeof(ModelViewProjection), 0u, &data).Check();
        _ = new Span<ModelViewProjection>(data, 1)[0] = modelViewProjection;
        vk.UnmapMemory(device, uniformMemory);
    }

    private void SubmitWork(void* submitInfoPNext = null)
    {
        fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
        {
            var submitInfo = new SubmitInfo(pNext: submitInfoPNext, pCommandBuffers: commandBufferPtr, commandBufferCount: 1u);

            vk.QueueSubmit(queue, 1u, in submitInfo, fence).Check();
            vk.QueueWaitIdle(queue).Check();
            vk.ResetFences(device, 1u, fence).Check();
        }
    }

    private void SubmitWork(KeyedMutexSyncInfo keyedMutexSyncInfo)
    {
        fixed (DeviceMemory* directMemoryPtr = &directImageMemory)
        {
            var keyedMutexInfo = new Win32KeyedMutexAcquireReleaseInfoKHR
            (
                acquireCount: 1,
                pAcquireSyncs: directMemoryPtr,
                pAcquireKeys: &keyedMutexSyncInfo.AcquireKey,
                pAcquireTimeouts: &keyedMutexSyncInfo.Timeout,
                releaseCount: 1,
                pReleaseSyncs: directMemoryPtr,
                pReleaseKeys: &keyedMutexSyncInfo.ReleaseKey
            );
            SubmitWork(&keyedMutexInfo);
        }
    }

    public void Draw(float time)
    {
        UpdateModelViewProjection(time);
        SubmitWork();
    }

    public void Draw(float time, KeyedMutexSyncInfo keyedMutexSyncInfo)
    {
        UpdateModelViewProjection(time);
        SubmitWork(keyedMutexSyncInfo);
    }

    public void ReleaseSizeDependentResources()
    {
        vk.DestroyPipeline(device, pipeline, null);

        vk.DestroyFramebuffer(device, framebuffer, null);

        vk.DestroyImageView(device, colorView, null);
        vk.DestroyImageView(device, depthView, null);
        vk.DestroyImageView(device, directView, null);

        vk.DestroyImage(device, colorImage, null);
        vk.DestroyImage(device, depthImage, null);
        vk.DestroyImage(device, directImage, null);

        vk.FreeMemory(device, colorImageMemory, null);
        vk.FreeMemory(device, depthImageMemory, null);
        vk.FreeMemory(device, directImageMemory, null);

        fixed (CommandBuffer* commandBuffersPtr = &commandBuffer)
            vk.FreeCommandBuffers(device, commandPool, 1u, commandBuffersPtr);
    }

    public void Resize(nint directTextureHandle, uint width, uint height)
    {
        this.width = width;
        this.height = height;

        ReleaseSizeDependentResources();

        CreateImageViews(directTextureHandle);
        CreatePipeline();
        CreateFramebuffer();
        CreateCommandBuffer();

        modelViewProjection.Projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 180f * 45f, (float)width / height, 0.001f, 10000f);
    }

    public unsafe void Clear()
    {
        ReleaseSizeDependentResources();

        vk.DestroyBuffer(device, vertexBuffer, null);
        vk.DestroyBuffer(device, indexBuffer, null);
        vk.DestroyBuffer(device, uniformBuffer, null);

        vk.FreeMemory(device, vertexMemory, null);
        vk.FreeMemory(device, indexMemory, null);
        vk.FreeMemory(device, uniformMemory, null);

        vk.DestroyRenderPass(device, renderPass, null);

        vk.DestroyShaderModule(device, vertexShaderModule, null);
        vk.DestroyShaderModule(device, fragmentShaderModule, null);

        vk.DestroyDescriptorPool(device, descriptorPool, null);
        vk.DestroyDescriptorSetLayout(device, descriptorSetLayout, null);

        vk.DestroyCommandPool(device, commandPool, null);

        vk.DestroyPipelineLayout(device, pipelineLayout, null);

        vk.DestroyFence(device, fence, null);
        vk.DestroyDevice(device, null);
        vk.DestroyInstance(instance, null);
    }

    public int RotationSpeed { get; set; } = 1;
}
