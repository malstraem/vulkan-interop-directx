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

public unsafe class VulkanInterop
{
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

        public static VertexInputAttributeDescription[] GetAttributeDescriptions() => new[]
        {
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
        };
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
    private SampleCountFlags sampleCount = SampleCountFlags.Count8Bit;

    private static byte[] ReadBytes(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string name = assembly.GetName().Name!;

        using var stream = assembly.GetManifestResourceStream($"{name}.{filename}");
        using var reader = new BinaryReader(stream!);

        return reader.ReadBytes((int)stream!.Length);
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
            viewType: (ImageViewType)imageInfo.ImageType,
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
            handleTypes: ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit
        );

        var imageInfo = new ImageCreateInfo
        (
            imageType: ImageType.Type2D,
            format: targetFormat,
            samples: SampleCountFlags.None,
            usage: ImageUsageFlags.ColorAttachmentBit,
            mipLevels: 0u,
            arrayLayers: 0u,
            extent: new Extent3D(width: width, height: height, depth: 0u),
            pNext: &externalMemoryImageInfo
        );

        var importMemoryInfo = new ImportMemoryWin32HandleInfoKHR
        (
            handleType: ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit,
            handle: directTextureHandle
        );
        var memoryInfo = new MemoryAllocateInfo(pNext: &importMemoryInfo);

        vk.CreateImage(device, imageInfo, null, out directImage).Check();

        vk.AllocateMemory(device, memoryInfo, null, out directImageMemory).Check();
        vk.BindImageMemory(device, directImage, directImageMemory, 0ul).Check();

        var imageViewInfo = new ImageViewCreateInfo
        (
            image: directImage,
            viewType: ImageViewType.Type2D,
            format: targetFormat,
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
                polygonMode: PolygonMode.Fill,
                cullMode: CullModeFlags.BackBit,
                frontFace: FrontFace.Clockwise
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

        vk.BeginCommandBuffer(commandBuffer, new CommandBufferBeginInfo()).Check();

        var clearValues = stackalloc ClearValue[2]
        {
            new(color: new(0f, 0f, 0f, 1f)),
            new(depthStencil: new(1f, 0u))
        };

        var renderPassBeginInfo = new RenderPassBeginInfo
        (
            renderPass: renderPass,
            framebuffer: framebuffer,
            clearValueCount: 2u,
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

    public void Initialize(nint directTextureHandle, uint width, uint height, Format targetFormat, Stream modelStream)
    {
        this.width = width;
        this.height = height;

        this.targetFormat = targetFormat;

        #region Create instance
        var appInfo = new ApplicationInfo(apiVersion: Vk.Version10);
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

            if (CheckGraphicsQueue(physicalDevice, ref queueIndex) && CheckExternalMemoryExtension(physicalDevice))
            {
                this.physicalDevice = physicalDevice;
                break;
            }
        }

        if (physicalDevice.Handle == nint.Zero)
            throw new Exception("Suitable device not found");

        depthFormat = FindSupportedFormat([Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint], ImageTiling.Optimal, FormatFeatureFlags.DepthStencilAttachmentBit);

        vk.GetPhysicalDeviceProperties(physicalDevice, out var physicalDeviceProperties);

        var sampleCounts = physicalDeviceProperties.Limits.FramebufferDepthSampleCounts & physicalDeviceProperties.Limits.FramebufferColorSampleCounts;

        sampleCount = sampleCounts switch
        {
            _ when sampleCounts.HasFlag(SampleCountFlags.Count8Bit) => SampleCountFlags.Count8Bit,
            _ when sampleCounts.HasFlag(SampleCountFlags.Count4Bit) => SampleCountFlags.Count4Bit,
            _ when sampleCounts.HasFlag(SampleCountFlags.Count2Bit) => SampleCountFlags.Count2Bit,
            _ => SampleCountFlags.Count1Bit
        };

        Console.WriteLine($"{Encoding.UTF8.GetString(physicalDeviceProperties.DeviceName, 256).Trim('\0')} having {interopExtensionName} extension: 0x{physicalDevice.Handle:X8}");
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

        vk.CreateFence(device, new FenceCreateInfo(), null, out fence).Check();

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
            Format = Format.B8G8R8A8Unorm,
            Samples = sampleCount,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            FinalLayout = ImageLayout.ColorAttachmentOptimal
        };

        var colorAttachmentResolveDescription = new AttachmentDescription
        {
            Format = Format.B8G8R8A8Unorm,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        var depthAttachmentDescription = new AttachmentDescription
        {
            Format = depthFormat,
            Samples = sampleCount,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        };

        var colorAttachmentReference = new AttachmentReference(layout: ImageLayout.ColorAttachmentOptimal);
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

        indices = modelIndices.ToArray();
        vertices = modelVertices.ToArray();

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
            Type = DescriptorType.UniformBufferDynamic,
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

    private void SubmitWork()
    {
        fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
        {
            var submitInfo = new SubmitInfo(pCommandBuffers: commandBufferPtr, commandBufferCount: 1u);

            vk.QueueSubmit(queue, 1u, in submitInfo, fence).Check();
            vk.QueueWaitIdle(queue).Check();
        }
    }

    public void Draw(float time)
    {
        UpdateModelViewProjection(time);
        SubmitWork();
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

        vk.DestroyFence(device, fence, null);
        vk.DestroyDevice(device, null);
        vk.DestroyInstance(instance, null);
    }

    public int RotationSpeed { get; set; } = 1;
}
