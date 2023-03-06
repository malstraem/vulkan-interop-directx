using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Interop.Vulkan
{
    public static class Log
    {
        public static void Info(string info)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(info);
        }
    }

    public unsafe class VulkanInterop
    {
        private readonly struct Vertex
        {
            public Vertex(in Vector3 position, in Vector3 color)
            {
                Position = position;
                Color = color;
            }

            public readonly Vector3 Position;

            public readonly Vector3 Color;

            public static VertexInputBindingDescription GetBindingDescription()
            {
                VertexInputBindingDescription bindingDescription = new()
                {
                    Binding = 0,
                    Stride = (uint)Unsafe.SizeOf<Vertex>(),
                    InputRate = VertexInputRate.Vertex,
                };

                return bindingDescription;
            }

            public static VertexInputAttributeDescription[] GetAttributeDescriptions()
            {
                var attributeDescriptions = new[]
                {
                    new VertexInputAttributeDescription()
                    {
                        Binding = 0,
                        Location = 0,
                        Format = Format.R32G32Sfloat,
                        Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Position)),
                    },
                    new VertexInputAttributeDescription()
                    {
                        Binding = 0,
                        Location = 1,
                        Format = Format.R32G32B32Sfloat,
                        Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Color)),
                    }
                };

                return attributeDescriptions;
            }
        }

        private struct ModelViewProjection
        {
            public Matrix4x4 Model;
            public Matrix4x4 View;
            public Matrix4x4 Projection;
        }

        private readonly Vertex[] vertices =
        {
            #region Cube faces
            new(new(-0.5f,  0.5f, -0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new( 0.5f, -0.5f, -0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new(-0.5f, -0.5f, -0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new( 0.5f,  0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),

            new(new(0.5f, -0.5f, -0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new(0.5f,  0.5f,  0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new(0.5f, -0.5f,  0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new(0.5f,  0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),

            new(new(-0.5f,  0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new(-0.5f, -0.5f, -0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new(-0.5f, -0.5f,  0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new(-0.5f,  0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),

            new(new( 0.5f,  0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new(-0.5f, -0.5f,  0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new( 0.5f, -0.5f,  0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new(-0.5f,  0.5f,  0.5f), new(0.0f, 1.0f, 0.0f)),

            new(new(-0.5f, 0.5f, -0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new( 0.5f, 0.5f,  0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new( 0.5f, 0.5f, -0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new(-0.5f, 0.5f,  0.5f), new(0.0f, 1.0f, 0.0f)),

            new(new( 0.5f, -0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new(-0.5f, -0.5f, -0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new( 0.5f, -0.5f, -0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new(-0.5f, -0.5f,  0.5f), new(0.0f, 1.0f, 0.0f))
            #endregion
        };

        private readonly uint[] indices =
        {
            #region First to second indices blocks
            0u, 1u, 2u,
            0u, 3u, 1u,

            4u, 5u, 6u,
            4u, 7u, 5u,

            8u, 9u, 10u,
            8u, 11u, 9u,

            12u, 13u, 14u,
            12u, 15u, 13u,

            16u, 17u, 18u,
            16u, 19u, 17u,

            20u, 21u, 22u,
            20u, 23u, 21u
            #endregion
        };

        private uint width;
        private uint height;

        private readonly Vk vk = Vk.GetApi();

        private Instance instance;

        private Device device;
        private PhysicalDevice physicalDevice;

        private Queue queue;

        private Image image;
        private ImageView imageView;

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

        private DeviceMemory vertexMemory, indexMemory, uniformMemory;

        private ShaderModule vertexShaderModule, fragmentShaderModule;
        private static byte[] ReadBytes(string filename)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string name = assembly.GetName().Name;

            using (var stream = assembly.GetManifestResourceStream($"{name}.{filename}"))
            using (var reader = new BinaryReader(stream))

            return reader.ReadBytes((int)stream.Length);
        }

        private uint GetQueueIndex()
        {
            uint queueFamilityCount = 0u;
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilityCount, null);

            var queueFamilies = new QueueFamilyProperties[queueFamilityCount];

            fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
                vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilityCount, queueFamiliesPtr);

            uint i = 0u;
            foreach (var queueFamily in queueFamilies)
            {
                if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                    break;
                i++;
            }
            return i;
        }

        private uint GetMemoryTypeIndex(uint typeBits, MemoryPropertyFlags memoryPropertyFlags)
        {
            vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memoryProperties);

            for (int i = 0; i < memoryProperties.MemoryTypeCount; i++)
            {
                if ((typeBits & (1 << i)) != 0 && (memoryProperties.MemoryTypes[i].PropertyFlags & memoryPropertyFlags) == memoryPropertyFlags)
                    return (uint)i;
            }

            throw new Exception("memory type not found");
        }

        private void CreateBuffer(BufferUsageFlags bufferUsage, MemoryPropertyFlags memoryProperties, out Buffer buffer, out DeviceMemory deviceMemory, ulong size)
        {
            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Usage = bufferUsage,
                Size = size,
                SharingMode = SharingMode.Exclusive
            };

            _ = vk.CreateBuffer(device, bufferCreateInfo, null, out buffer);

            vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);

            var memoryAllocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = GetMemoryTypeIndex(memoryRequirements.MemoryTypeBits, memoryProperties)
            };

            _ = vk.AllocateMemory(device, memoryAllocateInfo, null, out deviceMemory);

            _ = vk.BindBufferMemory(device, buffer, deviceMemory, 0ul);

            void* data;
            _ = vk.MapMemory(device, deviceMemory, 0ul, size, 0u, &data);
            vertices.AsSpan().CopyTo(new Span<Vertex>(data, vertices.Length));
            vk.UnmapMemory(device, deviceMemory);
        }

        private void CreateImageView(nint directTextureHandle)
        {
            #region Create image using external memory with DirectX texture handle
            var externalMemoryImageInfo = new ExternalMemoryImageCreateInfo()
            {
                SType = StructureType.ExternalMemoryImageCreateInfoKhr,
                HandleTypes = ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit
            };

            var imageCreateInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                PNext = &externalMemoryImageInfo,
                ImageType = ImageType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                MipLevels = 1u,
                ArrayLayers = 1u,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit,
                SharingMode = SharingMode.Exclusive
            };

            imageCreateInfo.Extent.Width = width;
            imageCreateInfo.Extent.Height = height;
            imageCreateInfo.Extent.Depth = 1u;

            _ = vk.CreateImage(device, &imageCreateInfo, null, out image);

            vk.GetImageMemoryRequirements(device, image, out var memoryRequirements);

            uint memoryType = GetMemoryTypeIndex(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

            var importMemoryInfo = new ImportMemoryWin32HandleInfoKHR
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureKmtBit,
                Handle = directTextureHandle
            };

            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &importMemoryInfo,
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = memoryType
            };

            _ = vk.AllocateMemory(device, &allocInfo, null, out var deviceMemory);

            _ = vk.BindImageMemory(device, image, deviceMemory, 0ul);

            var imageViewCreateInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                ViewType = ImageViewType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Image = image
            };

            _ = vk.CreateImageView(device, imageViewCreateInfo, null, out imageView);
            #endregion
        }
        
        private void CreatePipeline()
        {
            #region Create pipeline
            var vertexShaderStageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertexShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main")
            };

            var fragmentShaderStageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragmentShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main")
            };

            var shaderStages = stackalloc[]
            {
                vertexShaderStageInfo,
                fragmentShaderStageInfo
            };

            var bindingDescription = Vertex.GetBindingDescription();
            var attributeDescriptions = Vertex.GetAttributeDescriptions();

            fixed (VertexInputAttributeDescription* attributeDescriptionsPtr = attributeDescriptions)
            fixed (DescriptorSetLayout* descriptorSetLayoutPtr = &descriptorSetLayout)
            {
                var vertexInputStateCreateInfo = new PipelineVertexInputStateCreateInfo()
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1u,
                    VertexAttributeDescriptionCount = (uint)attributeDescriptions.Length,
                    PVertexBindingDescriptions = &bindingDescription,
                    PVertexAttributeDescriptions = attributeDescriptionsPtr
                };

                var inputAssemblyStateCreateInfo = new PipelineInputAssemblyStateCreateInfo()
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                    PrimitiveRestartEnable = false
                };

                var viewport = new Viewport()
                {
                    X = 0f,
                    Y = 0f,
                    Width = width,
                    Height = height,
                    MinDepth = 0f,
                    MaxDepth = 1f
                };

                var scissorRect = new Rect2D()
                {
                    Offset = { X = 0, Y = 0 },
                    Extent = new Extent2D(width, height)
                };

                var viewportStateCreateInfo = new PipelineViewportStateCreateInfo()
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1u,
                    PViewports = &viewport,
                    ScissorCount = 1u,
                    PScissors = &scissorRect
                };

                var rasterizationStateCreateInfo = new PipelineRasterizationStateCreateInfo()
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = false,
                    RasterizerDiscardEnable = false,
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1f,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.Clockwise,
                    DepthBiasEnable = false
                };

                var multisampleStateCreateInfo = new PipelineMultisampleStateCreateInfo()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    SampleShadingEnable = false,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                var colorBlendAttachmentState = new PipelineColorBlendAttachmentState()
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                    BlendEnable = false
                };

                var colorBlendStateCreateInfo = new PipelineColorBlendStateCreateInfo()
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOp = LogicOp.Copy,
                    AttachmentCount = 1u,
                    PAttachments = &colorBlendAttachmentState
                };

                var layoutCreateInfo = new PipelineLayoutCreateInfo()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 1u,
                    PSetLayouts = descriptorSetLayoutPtr
                };

                _ = vk.CreatePipelineLayout(device, layoutCreateInfo, null, out pipelineLayout);

                var pipelineCreateInfo = new GraphicsPipelineCreateInfo()
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2u,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInputStateCreateInfo,
                    PInputAssemblyState = &inputAssemblyStateCreateInfo,
                    PViewportState = &viewportStateCreateInfo,
                    PRasterizationState = &rasterizationStateCreateInfo,
                    PMultisampleState = &multisampleStateCreateInfo,
                    PColorBlendState = &colorBlendStateCreateInfo,
                    Layout = pipelineLayout,
                    RenderPass = renderPass
                };

                _ = vk.CreateGraphicsPipelines(device, default, 1u, pipelineCreateInfo, null, out pipeline);
            }

            SilkMarshal.Free((nint)vertexShaderStageInfo.PName);
            SilkMarshal.Free((nint)fragmentShaderStageInfo.PName);
            #endregion
        }

        private void CreateFramebuffer()
        {
            fixed (ImageView* imageViewPtr = &imageView)
            {
                var framebufferCreateInfo = new FramebufferCreateInfo()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = 1u,
                    PAttachments = imageViewPtr,
                    Width = width,
                    Height = height,
                    Layers = 1u
                };

                _ = vk.CreateFramebuffer(device, framebufferCreateInfo, null, out framebuffer);
            }
        }

        private void CreateCommandBuffer()
        {
            var commandBufferAllocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1u
            };

            _ = vk.AllocateCommandBuffers(device, commandBufferAllocateInfo, out commandBuffer);

            var commandBufferBeginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };

            _ = vk.BeginCommandBuffer(commandBuffer, commandBufferBeginInfo);

            var clearColor = new ClearValue { Color = { Float32_0 = 0f, Float32_1 = 0f, Float32_2 = 0f, Float32_3 = 1f } };

            var renderPassBeginInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                ClearValueCount = 1u,
                PClearValues = &clearColor,
                RenderArea =
                {
                    Offset = { X = 0, Y = 0 },
                    Extent = new Extent2D(width, height)
                }
            };

            vk.CmdBeginRenderPass(commandBuffer, renderPassBeginInfo, SubpassContents.Inline);

            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

            vk.CmdBindVertexBuffers(commandBuffer, 0u, 1u, vertexBuffer, 0ul);

            /*vk.CmdBindIndexBuffer(commandBuffer, indexBuffer, 0ul, IndexType.Uint32);

            vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 0u, 1u, descriptorSet, 0u, null);

            vk.CmdDrawIndexed(commandBuffer, (uint)indices.Length, 1u, 0u, 0, 0u);*/

            vk.CmdDraw(commandBuffer, (uint)vertices.Length, 1u, 0u, 0u);

            vk.CmdEndRenderPass(commandBuffer);

            _ = vk.EndCommandBuffer(commandBuffer);
        }

        private void UpdateModelViewProjection(float time)
        {
            var mvp = new ModelViewProjection()
            {
                Model = Matrix4x4.Identity * Matrix4x4.CreateFromAxisAngle(new Vector3(0f, 0f, 1f), time * 90.0f * MathF.PI / 180f),
                View = Matrix4x4.CreateLookAt(new Vector3(2f, 2f, 2f), new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f)),
                Projection = Matrix4x4.CreatePerspectiveFieldOfView(45.0f * MathF.PI / 180f, width / height, 0.1f, 10.0f),
            };

            mvp.Projection.M22 *= -1;

            void* data;
            _ = vk.MapMemory(device, uniformMemory, 0ul, (ulong)sizeof(ModelViewProjection), 0, &data);
            new Span<ModelViewProjection>(data, 1)[0] = mvp;
            vk.UnmapMemory(device, uniformMemory);
        }

        private void SubmitWork(CommandBuffer cmdBuffer, Queue queue)
        {
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1u,
                PCommandBuffers = &cmdBuffer
            };

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };

            _ = vk.CreateFence(device, fenceInfo, null, out var fence);
            _ = vk.QueueSubmit(queue, 1u, submitInfo, fence);
            _ = vk.WaitForFences(device, 1u, fence, true, ulong.MaxValue);

            vk.DestroyFence(device, fence, null);
        }

        private unsafe void Cleanup()
        {
            vk.DestroyBuffer(device, vertexBuffer, null);
            vk.DestroyBuffer(device, indexBuffer, null);

            vk.DestroyPipeline(device, pipeline, null);
            vk.DestroyFramebuffer(device, framebuffer, null);

            vk.DestroyPipeline(device, pipeline, null);

            vk.DestroyCommandPool(device, commandPool, null);
            vk.DestroyDevice(device, null);
            vk.DestroyInstance(instance, null);
        }

        private unsafe ShaderModule CreateShaderModule(byte[] code)
        {
            fixed (byte* codePtr = code)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)codePtr
                };

                _ = vk.CreateShaderModule(device, &createInfo, null, out var shaderModule);

                return shaderModule;
            }
        }

        public void Initialize(nint directTextureHandle, uint width, uint height)
        {
            this.width = width;
            this.height = height;

            #region Create instance
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                ApiVersion = Vk.Version13
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo
            };

            _ = vk.CreateInstance(createInfo, null, out instance);

            Log.Info($"Instance {instance.Handle} created");
            #endregion

            #region Create device
            uint deviceCount = 0u;

            _ = vk.EnumeratePhysicalDevices(instance, ref deviceCount, null);
            _ = vk.EnumeratePhysicalDevices(instance, ref deviceCount, ref physicalDevice);

            vk.GetPhysicalDeviceProperties(physicalDevice, out var physicalDeviceProperties);

            Log.Info($"GPU: {Encoding.UTF8.GetString(physicalDeviceProperties.DeviceName, 256)}");

            uint queueIndex = GetQueueIndex();

            using var mem = GlobalMemory.Allocate(sizeof(DeviceQueueCreateInfo));
            var deviceQueueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

            float queuePriority = 1f;

            deviceQueueCreateInfos[0] = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueIndex,
                QueueCount = 1u,
                PQueuePriorities = &queuePriority
            };

            var deviceCreateInfo = new DeviceCreateInfo()
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1u,
                PQueueCreateInfos = deviceQueueCreateInfos
            };

            _ = vk.CreateDevice(physicalDevice, deviceCreateInfo, null, out device);

            vk.GetDeviceQueue(device, queueIndex, 0u, out queue);
            #endregion

            #region Create command pool
            var commandPoolCreateInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueIndex
            };

            _ = vk.CreateCommandPool(device, commandPoolCreateInfo, null, out commandPool);
            #endregion

            #region Create shader modules
            byte[] vertShaderCode = ReadBytes("shaders.shader.vert.spv");
            byte[] fragShaderCode = ReadBytes("shaders.shader.frag.spv");

            vertexShaderModule = CreateShaderModule(vertShaderCode);
            fragmentShaderModule = CreateShaderModule(fragShaderCode);
            #endregion

            #region Create render pass
            var attachmentDescription = new AttachmentDescription
            {
                Format = Format.B8G8R8A8Unorm,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr
            };

            var attachmentReference = new AttachmentReference
            {
                Attachment = 0u,
                Layout = ImageLayout.ColorAttachmentOptimal
            };

            var subpassDescription = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1u,
                PColorAttachments = &attachmentReference
            };

            var subpassDependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit
            };

            var renderPassCreateInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1u,
                PAttachments = &attachmentDescription,
                SubpassCount = 1u,
                PSubpasses = &subpassDescription,
                DependencyCount = 1u,
                PDependencies = &subpassDependency,
            };

            _ = vk.CreateRenderPass(device, renderPassCreateInfo, null, out renderPass);
            #endregion

            #region Create descriptor pool
            var descriptorPoolSize = new DescriptorPoolSize()
            {
                Type = DescriptorType.UniformBuffer,
                DescriptorCount = 1u
            };

            var descriptorPoolCreateInfo = new DescriptorPoolCreateInfo()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1u,
                PPoolSizes = &descriptorPoolSize,
                MaxSets = 1u
            };

            _ = vk.CreateDescriptorPool(device, descriptorPoolCreateInfo, null, out descriptorPool);
            #endregion

            #region Create descriptor set layout
            var descriptorSetLayoutBinding = new DescriptorSetLayoutBinding()
            {
                Binding = 0u,
                DescriptorCount = 1u,
                DescriptorType = DescriptorType.UniformBuffer,
                StageFlags = ShaderStageFlags.VertexBit
            };

            var descriptorSetLayoutCreateInfo = new DescriptorSetLayoutCreateInfo()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1u,
                PBindings = &descriptorSetLayoutBinding
            };

            _ = vk.CreateDescriptorSetLayout(device, descriptorSetLayoutCreateInfo, null, out descriptorSetLayout);
            #endregion

            #region Create descriptor set
            fixed (DescriptorSetLayout* setLayoutPtr = &descriptorSetLayout)
            {
                var descriptorSetAllocateInfo = new DescriptorSetAllocateInfo()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = descriptorPool,
                    DescriptorSetCount = 1u,
                    PSetLayouts = setLayoutPtr
                };

                _ = vk.AllocateDescriptorSets(device, descriptorSetAllocateInfo, out descriptorSet);
            }

            var descriptorBufferInfo = new DescriptorBufferInfo()
            {
                Buffer = uniformBuffer,
                Range = (ulong)sizeof(ModelViewProjection)
            };

            var writeDescriptorSet = new WriteDescriptorSet()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = 0u,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1u,
                PBufferInfo = &descriptorBufferInfo
            };

            vk.UpdateDescriptorSets(device, 1u, writeDescriptorSet, 0u, null);
            #endregion

            CreateImageView(directTextureHandle);

            CreatePipeline();

            CreateFramebuffer();

            #region Create vertex, index and uniform buffers
            ulong vertexBufferSize = (ulong)(vertices.Length * sizeof(Vertex));
            ulong indexBufferSize = (ulong)(indices.Length * sizeof(uint));
            ulong mvpBufferSize = (ulong)sizeof(ModelViewProjection);

            CreateBuffer(BufferUsageFlags.VertexBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out vertexBuffer, out vertexMemory, vertexBufferSize);

            CreateBuffer(BufferUsageFlags.IndexBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out indexBuffer, out indexMemory, indexBufferSize);

            CreateBuffer(BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out uniformBuffer, out uniformMemory, mvpBufferSize);
            #endregion

            CreateCommandBuffer();
        }

        public void Draw(float time)
        {
            UpdateModelViewProjection(time);
            SubmitWork(commandBuffer, queue);
        }

        public void Resize(nint directTextureHandle, uint width, uint height)
        {
            this.width = width;
            this.height = height;

            vk.DestroyImage(device, image, null);
            vk.DestroyImageView(device, imageView, null);

            vk.DestroyFramebuffer(device, framebuffer, null);

            vk.DestroyPipeline(device, pipeline, null);
            vk.DestroyPipelineLayout(device, pipelineLayout, null);

            fixed (CommandBuffer* commandBuffersPtr = &commandBuffer)
                vk.FreeCommandBuffers(device, commandPool, 1u, commandBuffersPtr);

            CreateImageView(directTextureHandle);

            CreatePipeline();

            CreateFramebuffer();

            CreateCommandBuffer();
        }
    }
}