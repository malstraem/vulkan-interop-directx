using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace VulkanInterop
{
    public static class Log
    {
        public static void Info(string info)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(info);
        }
    }

    public unsafe class VulkanInteropApp
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

        private readonly Vertex[] vertices =
        {
            new Vertex (new Vector3(1f,  1f, 0f), new Vector3(1f, 0f, 0f)),
            new Vertex (new Vector3(-1f,  1f, 0f), new Vector3(0f, 1f, 0f)),
            new Vertex (new Vector3(0f,  -1f, 0f), new Vector3(0f, 0f, 1f)),
        };

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

        private RenderPass renderPass;

        private CommandPool commandPool;
        private CommandBuffer commandBuffer;

        private Buffer vertexBuffer, indexBuffer;

        private DeviceMemory vertexMemory, indexMemory;

        private static byte[] ReadBytes(string filename)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string name = assembly.GetName().FullName;

            using (var stream = assembly.GetManifestResourceStream(filename))
            using (var reader = new BinaryReader(stream))

                return reader.ReadBytes((int)stream.Length);
        }

        private uint GetQueueIndex(PhysicalDevice device)
        {
            uint queueFamilityCount = 0u;
            vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilityCount, null);

            var queueFamilies = new QueueFamilyProperties[queueFamilityCount];

            fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            {
                vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilityCount, queueFamiliesPtr);
            }

            uint i = 0u;
            foreach (var queueFamily in queueFamilies)
            {
                if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                    break;

                i++;
            }
            return i;
        }

        uint GetMemoryTypeIndex(uint typeBits, MemoryPropertyFlags memoryPropertyFlags)
        {
            vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memProperties);

            for (int i = 0; i < memProperties.MemoryTypeCount; i++)
            {
                if ((typeBits & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & memoryPropertyFlags) == memoryPropertyFlags)
                    return (uint)i;
            }

            throw new Exception("memory type not found");
        }

        private void CreateBuffer(BufferUsageFlags bufferUsage, MemoryPropertyFlags memoryProperties, ref Buffer buffer, ref DeviceMemory deviceMemory, ulong size)
        {
            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Usage = bufferUsage,
                Size = size,
                SharingMode = SharingMode.Exclusive
            };

            fixed (Buffer* bufferPtr = &buffer)
            {
                if (vk.CreateBuffer(device, bufferCreateInfo, null, bufferPtr) != Result.Success)
                    throw new Exception("failed to create vertex buffer!");
            }

            vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);

            var memoryAllocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = GetMemoryTypeIndex(memoryRequirements.MemoryTypeBits, memoryProperties)
            };

            fixed (DeviceMemory* bufferMemoryPtr = &deviceMemory)
            {
                if (vk.AllocateMemory(device, memoryAllocateInfo, null, bufferMemoryPtr) != Result.Success)
                    throw new Exception("failed to allocate vertex buffer memory!");
            }

            vk.BindBufferMemory(device, buffer, deviceMemory, 0);
        }

        private void SubmitWork(CommandBuffer cmdBuffer, Queue queue)
        {
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmdBuffer,
            };

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };

            vk.CreateFence(device, fenceInfo, null, out var fence);

            vk.QueueSubmit(queue, 1, submitInfo, fence);

            vk.WaitForFences(device, 1, fence, true, ulong.MaxValue);
            vk.DestroyFence(device, fence, null);
        }

        private void CreateBuffers()
        {
            #region Create vertex and index buffers
            int[] indices = { 0, 1, 2 };

            ulong vertexBufferSize = (ulong)(Unsafe.SizeOf<Vertex>() * vertices.Length);
            ulong indexBufferSize = (ulong)(indices.Length * sizeof(int));

            DeviceMemory stagingMemory = default;
            Buffer stagingBuffer = default;

            CreateBuffer(BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                ref stagingBuffer, ref stagingMemory, vertexBufferSize);

            void* data;
            vk.MapMemory(device, stagingMemory, 0, vertexBufferSize, 0, &data);
            vertices.AsSpan().CopyTo(new Span<Vertex>(data, vertices.Length));
            vk.UnmapMemory(device, stagingMemory);

            CreateBuffer(BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit,
                ref vertexBuffer, ref vertexMemory, vertexBufferSize);

            var commandBufferAllocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            vk.AllocateCommandBuffers(device, commandBufferAllocateInfo, out var copyCommandBuffer);

            var commandBufferBeginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };

            vk.BeginCommandBuffer(copyCommandBuffer, &commandBufferBeginInfo);

            var copyRegion = new BufferCopy { Size = vertexBufferSize };

            vk.CmdCopyBuffer(copyCommandBuffer, stagingBuffer, vertexBuffer, 1, &copyRegion);

            vk.EndCommandBuffer(copyCommandBuffer);

            SubmitWork(copyCommandBuffer, queue);

            vk.DestroyBuffer(device, stagingBuffer, null);
            vk.FreeMemory(device, stagingMemory, null);

/*            // Indices
               CreateBuffer(BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                   out stagingBuffer, out stagingMemory, indexBufferSize, &indices);

               CreateBuffer(BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit,
                   out var indexBuffer, out var indexMemory, indexBufferSize);

            vk.BeginCommandBuffer(copyCommandBuffer, commandBufferBeginInfo);

            copyRegion.Size = indexBufferSize;

            //vk.CmdCopyBuffer(copyCommandBuffer, stagingBuffer, indexBuffer, 1, &copyRegion);
            vk.EndCommandBuffer(copyCommandBuffer);

            SubmitWork(copyCommandBuffer, queue);

            vk.DestroyBuffer(device, stagingBuffer, null);
            vk.FreeMemory(device, stagingMemory, null);*/
            #endregion
        }

        private void CreateImage(nint directTextureHandle, uint width, uint height)
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
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit,
                Flags = ImageCreateFlags.None,
                SharingMode = SharingMode.Exclusive
            };

            imageCreateInfo.Extent.Width = width;
            imageCreateInfo.Extent.Height = height;
            imageCreateInfo.Extent.Depth = 1;

            var result = vk.CreateImage(device, &imageCreateInfo, null, out image);

            vk.GetImageMemoryRequirements(device, image, out var memoryRequirements);

            uint memoryType = GetMemoryTypeIndex(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);

            var importMemoryInfo = new ImportMemoryWin32HandleInfoKHR
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                PNext = null,
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

            result = vk.AllocateMemory(device, &allocInfo, null, out var deviceMemory);

            result = vk.BindImageMemory(device, image, deviceMemory, 0);

            var imageViewCreateInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                ViewType = ImageViewType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Image = image
            };

            result = vk.CreateImageView(device, imageViewCreateInfo, null, out imageView);
            #endregion
        }
        
        private void CreatePipeline(uint width, uint height)
        {
            #region Create pipeline
            byte[] vertShaderCode = ReadBytes("VulkanInteropApp.shaders.shader.vert.spv");
            byte[] fragShaderCode = ReadBytes("VulkanInteropApp.shaders.shader.frag.spv");

            var vertShaderModule = CreateShaderModule(vertShaderCode);
            var fragShaderModule = CreateShaderModule(fragShaderCode);

            var vertShaderStageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main")
            };

            var fragShaderStageInfo = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main")
            };

            var shaderStages = stackalloc[]
            {
                vertShaderStageInfo,
                fragShaderStageInfo
            };

            var bindingDescription = Vertex.GetBindingDescription();
            var attributeDescriptions = Vertex.GetAttributeDescriptions();

            fixed (VertexInputAttributeDescription* attributeDescriptionsPtr = attributeDescriptions)
            {
                var vertexInputInfo = new PipelineVertexInputStateCreateInfo()
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    VertexAttributeDescriptionCount = (uint)attributeDescriptions.Length,
                    PVertexBindingDescriptions = &bindingDescription,
                    PVertexAttributeDescriptions = attributeDescriptionsPtr
                };

                var inputAssembly = new PipelineInputAssemblyStateCreateInfo()
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                    PrimitiveRestartEnable = false
                };

                var viewport = new Viewport()
                {
                    X = 0,
                    Y = 0,
                    Width = width,
                    Height = height,
                    MinDepth = 0,
                    MaxDepth = 1
                };

                var scissor = new Rect2D()
                {
                    Offset = { X = 0, Y = 0 },
                    Extent = new Extent2D(width, height)
                };

                var viewportState = new PipelineViewportStateCreateInfo()
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo()
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = false,
                    RasterizerDiscardEnable = false,
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1,
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.Clockwise,
                    DepthBiasEnable = false
                };

                var multisampling = new PipelineMultisampleStateCreateInfo()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    SampleShadingEnable = false,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                var colorBlendAttachment = new PipelineColorBlendAttachmentState()
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                    BlendEnable = false
                };

                var colorBlending = new PipelineColorBlendStateCreateInfo()
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    LogicOp = LogicOp.Copy,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment
                };

                colorBlending.BlendConstants[0] = 0;
                colorBlending.BlendConstants[1] = 0;
                colorBlending.BlendConstants[2] = 0;
                colorBlending.BlendConstants[3] = 0;

                var pipelineLayoutInfo = new PipelineLayoutCreateInfo()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 0,
                    PushConstantRangeCount = 0
                };

                if (vk.CreatePipelineLayout(device, pipelineLayoutInfo, null, out pipelineLayout) != Result.Success)
                    throw new Exception("failed to create pipeline layout!");

                var pipelineInfo = new GraphicsPipelineCreateInfo()
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInputInfo,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisampling,
                    PColorBlendState = &colorBlending,
                    Layout = pipelineLayout,
                    RenderPass = renderPass,
                    Subpass = 0,
                    BasePipelineHandle = default
                };

                if (vk.CreateGraphicsPipelines(device, default, 1, pipelineInfo, null, out pipeline) != Result.Success)
                    throw new Exception("failed to create graphics pipeline!");
            }
            #endregion
        }

        private void CreateFramebuffer(uint width, uint height)
        {
            ImageView* imageViewPtr;

            fixed (ImageView* ptr = &imageView)
                imageViewPtr = ptr;

            FramebufferCreateInfo framebufferInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = imageViewPtr,
                Width = width,
                Height = height,
                Layers = 1,
            };

            if (vk.CreateFramebuffer(device, framebufferInfo, null, out framebuffer) != Result.Success)
                throw new Exception("failed to create framebuffer!");
        }

        private void CreateCommandBuffer(uint width, uint height)
        {
            var commandBufferAllocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1u,
            };

            var result = vk.AllocateCommandBuffers(device, commandBufferAllocateInfo, out commandBuffer);

            var commandBufferBeginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };

            result = vk.BeginCommandBuffer(commandBuffer, commandBufferBeginInfo);

            var clearColor = new ClearValue { Color = { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 } };

            var renderPassBeginInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                ClearValueCount = 1,
                PClearValues = &clearColor,
                RenderArea =
                {
                    Offset = { X = 0, Y = 0 },
                    Extent = new Extent2D(width, height)
                }
            };

            vk.CmdBeginRenderPass(commandBuffer, renderPassBeginInfo, SubpassContents.Inline);

            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

            var vertexBuffers = new Buffer[] { vertexBuffer };
            ulong[] offsets = { 0ul };

            fixed (ulong* offsetsPtr = offsets)
            fixed (Buffer* vertexBuffersPtr = vertexBuffers)
            {
                vk.CmdBindVertexBuffers(commandBuffer, 0, 1, vertexBuffersPtr, offsetsPtr);
            }

            vk.CmdDraw(commandBuffer, (uint)vertices.Length, 1, 0, 0);

            vk.CmdEndRenderPass(commandBuffer);

            if (vk.EndCommandBuffer(commandBuffer) != Result.Success)
                throw new Exception("failed to record command buffer!");
        }

        private unsafe void Cleanup()
        {
            vk.DestroyBuffer(device, vertexBuffer, null);
            //vk.DestroyBuffer(device, indexBuffer, null);

            vk.DestroyPipeline(device, pipeline, null);
            vk.DestroyFramebuffer(device, framebuffer, null);

            vk.DestroyPipeline(device, pipeline, null);

            vk.DestroyCommandPool(device, commandPool, null);
            vk.DestroyDevice(device, null);
            vk.DestroyInstance(instance, null);
        }

        private unsafe ShaderModule CreateShaderModule(byte[] code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length
            };

            fixed (byte* codePtr = code)
            {
                createInfo.PCode = (uint*)codePtr;
            }

            var shaderModule = new ShaderModule();
            return vk.CreateShaderModule(device, &createInfo, null, &shaderModule) != Result.Success
                ? throw new Exception("failed to create shader module!")
                : shaderModule;
        }

        public void Initialize(nint directTextureHandle, uint width, uint height)
        {
            #region Create instance
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Hello Triangle"),
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = (byte*)Marshal.StringToHGlobalAnsi("No Engine"),
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = Vk.Version13
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
            };

            vk.CreateInstance(createInfo, null, out instance);

            Log.Info($"Instance {instance.Handle} created");

            Marshal.FreeHGlobal((nint)appInfo.PApplicationName);
            Marshal.FreeHGlobal((nint)appInfo.PEngineName);
            #endregion

            #region Create device
            uint deviceCount = 0u;

            var result = vk.EnumeratePhysicalDevices(instance, ref deviceCount, null);

            result = vk.EnumeratePhysicalDevices(instance, ref deviceCount, ref physicalDevice);

            vk.GetPhysicalDeviceProperties(physicalDevice, out var physicalDeviceProperties);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"GPU: {Encoding.UTF8.GetString(physicalDeviceProperties.DeviceName, 256)}");

            uint queueIndex = GetQueueIndex(physicalDevice);

            using var mem = GlobalMemory.Allocate(sizeof(DeviceQueueCreateInfo));
            var deviceQueueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

            float queuePriority = 1.0f;

            deviceQueueCreateInfos[0] = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueIndex,
                QueueCount = 1,
                PQueuePriorities = &queuePriority
            };

            DeviceCreateInfo deviceCreateinfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1u,
                PQueueCreateInfos = deviceQueueCreateInfos,
            };

            vk.CreateDevice(physicalDevice, deviceCreateinfo, null, out device);

            vk.GetDeviceQueue(device, queueIndex, 0, out queue);
            #endregion

            #region Create command pool
            var commandPoolCreateInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueIndex
            };

            vk.CreateCommandPool(device, commandPoolCreateInfo, null, out commandPool);
            #endregion

            CreateBuffers();

            #region Create render pass
            var colorAttachment = new AttachmentDescription
            {
                Format = Format.B8G8R8A8Unorm,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
            };

            var colorAttachmentRef = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachmentRef,
            };

            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = 0,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit
            };

            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };

            if (vk.CreateRenderPass(device, renderPassInfo, null, out renderPass) != Result.Success)
                throw new Exception("failed to create render pass!");
            #endregion

            CreatePipeline(width, height);

            CreateImage(directTextureHandle, width, height);

            CreateFramebuffer(width, height);

            CreateCommandBuffer(width, height);
        }

        public void Draw() => SubmitWork(commandBuffer, queue);

        public void Resize(nint directTextureHandle, uint width, uint height)
        {
            CreatePipeline(width, height);

            CreateImage(directTextureHandle, width, height);

            CreateFramebuffer(width, height);

            CreateCommandBuffer(width, height);
        }
    }
}