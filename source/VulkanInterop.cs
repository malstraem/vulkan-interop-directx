using System.Numerics;
using System.Reflection;
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
        private readonly struct VertexPositionColor
        {
            public VertexPositionColor(in Vector3 position, in Vector3 color)
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
                    Binding = 0u,
                    Stride = (uint)sizeof(VertexPositionColor),
                    InputRate = VertexInputRate.Vertex
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
                        Format = Format.R32G32B32Sfloat,
                        Offset = (uint)Marshal.OffsetOf<VertexPositionColor>(nameof(Position))
                    },
                    new VertexInputAttributeDescription()
                    {
                        Binding = 0,
                        Location = 1,
                        Format = Format.R32G32B32Sfloat,
                        Offset = (uint)Marshal.OffsetOf<VertexPositionColor>(nameof(Color))
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

        private readonly VertexPositionColor[] vertices =
        {
            #region Cube faces
            new(new(-0.5f,  0.5f, -0.5f), new(1f, 0f, 0f)),
            new(new( 0.5f, -0.5f, -0.5f), new(1f, 0f, 1f)),
            new(new(-0.5f, -0.5f, -0.5f), new(0f, 0f, 1f)),
            new(new( 0.5f,  0.5f, -0.5f), new(0f, 1f, 0f)),

            new(new(0.5f, -0.5f, -0.5f), new(1f, 0f, 0f)),
            new(new(0.5f,  0.5f,  0.5f), new(1f, 0f, 1f)),
            new(new(0.5f, -0.5f,  0.5f), new(0f, 0f, 1f)),
            new(new(0.5f,  0.5f, -0.5f), new(0f, 1f, 0f)),

            new(new(-0.5f,  0.5f,  0.5f), new(1f, 0f, 0f)),
            new(new(-0.5f, -0.5f, -0.5f), new(1f, 0f, 1f)),
            new(new(-0.5f, -0.5f,  0.5f), new(0f, 0f, 1f)),
            new(new(-0.5f,  0.5f, -0.5f), new(0f, 1f, 0f)),

            new(new( 0.5f,  0.5f,  0.5f), new(1f, 0f, 0f)),
            new(new(-0.5f, -0.5f,  0.5f), new(1f, 0f, 1f)),
            new(new( 0.5f, -0.5f,  0.5f), new(0f, 0f, 1f)),
            new(new(-0.5f,  0.5f,  0.5f), new(0f, 1f, 0f)),

            new(new(-0.5f, 0.5f, -0.5f), new(1f, 0f, 0f)),
            new(new( 0.5f, 0.5f,  0.5f), new(1f, 0f, 1f)),
            new(new( 0.5f, 0.5f, -0.5f), new(0f, 0f, 1f)),
            new(new(-0.5f, 0.5f,  0.5f), new(0f, 1f, 0f)),

            new(new( 0.5f, -0.5f,  0.5f), new(1f, 0f, 0f)),
            new(new(-0.5f, -0.5f, -0.5f), new(1f, 0f, 1f)),
            new(new( 0.5f, -0.5f, -0.5f), new(0f, 0f, 1f)),
            new(new(-0.5f, -0.5f,  0.5f), new(0f, 1f, 0f))
            #endregion
        };

        private readonly ushort[] indices =
        {
            #region Counter clockwise indices
            0, 1, 2,
            0, 3, 1,

            4, 5, 6,
            4, 7, 5,

            8, 9, 10,
            8, 11, 9,

            12, 13, 14,
            12, 15, 13,

            16, 17, 18,
            16, 19, 17,

            20, 21, 22,
            20, 23, 21
            #endregion
        };

        private uint width;
        private uint height;

        private readonly Vk vk = Vk.GetApi();

        private Instance instance;

        private Device device;
        private PhysicalDevice physicalDevice;

        private Queue queue;

        private Fence fence;

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

        private static void Check(Result result)
        {
            if(result is not Result.Success)
                throw new Exception($"failed to call vulkan function - {result}");
        }

        private static byte[] ReadBytes(string filename)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string name = assembly.GetName().Name!;

            using (var stream = assembly.GetManifestResourceStream($"{name}.{filename}"))
            using (var reader = new BinaryReader(stream!))

            return reader.ReadBytes((int)stream!.Length);
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

                Check(vk.CreateShaderModule(device, &createInfo, null, out var shaderModule));

                return shaderModule;
            }
        }

        private void CreateBuffer(BufferUsageFlags bufferUsage, MemoryPropertyFlags memoryProperties, out Buffer buffer, out DeviceMemory deviceMemory, ulong size)
        {
            var bufferCreateInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                SharingMode = SharingMode.Exclusive,
                Usage = bufferUsage,
                Size = size
            };

            Check(vk.CreateBuffer(device, bufferCreateInfo, null, out buffer));

            vk.GetBufferMemoryRequirements(device, buffer, out var memoryRequirements);

            var memoryAllocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = GetMemoryTypeIndex(memoryRequirements.MemoryTypeBits, memoryProperties)
            };

            Check(vk.AllocateMemory(device, memoryAllocateInfo, null, out deviceMemory));

            Check(vk.BindBufferMemory(device, buffer, deviceMemory, 0ul));
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

            Check(vk.CreateImage(device, &imageCreateInfo, null, out image));

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

            Check(vk.AllocateMemory(device, &allocInfo, null, out var deviceMemory));

            Check(vk.BindImageMemory(device, image, deviceMemory, 0ul));

            var imageViewCreateInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                ViewType = ImageViewType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Image = image
            };

            Check(vk.CreateImageView(device, imageViewCreateInfo, null, out imageView));
            #endregion
        }
        
        private void CreatePipeline()
        {
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

            var bindingDescription = VertexPositionColor.GetBindingDescription();
            var attributeDescriptions = VertexPositionColor.GetAttributeDescriptions();

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
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1f,
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.CounterClockwise
                };

                var multisampleStateCreateInfo = new PipelineMultisampleStateCreateInfo()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                var colorBlendAttachmentState = new PipelineColorBlendAttachmentState()
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
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

                Check(vk.CreatePipelineLayout(device, layoutCreateInfo, null, out pipelineLayout));

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

                Check(vk.CreateGraphicsPipelines(device, default, 1u, pipelineCreateInfo, null, out pipeline));
            }

            _ = SilkMarshal.Free((nint)vertexShaderStageInfo.PName);
            _ = SilkMarshal.Free((nint)fragmentShaderStageInfo.PName);
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

                Check(vk.CreateFramebuffer(device, framebufferCreateInfo, null, out framebuffer));
            }
        }

        private void CreateCommandBuffer()
        {
            var commandBufferAllocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1u,
                CommandPool = commandPool
            };

            Check(vk.AllocateCommandBuffers(device, commandBufferAllocateInfo, out commandBuffer));

            var commandBufferBeginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };

            Check(vk.BeginCommandBuffer(commandBuffer, commandBufferBeginInfo));

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

            vk.CmdBindIndexBuffer(commandBuffer, indexBuffer, 0ul, IndexType.Uint16);

            vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, pipelineLayout, 0u, 1u, descriptorSet, 0u, null);

            vk.CmdDrawIndexed(commandBuffer, (uint)indices.Length, 1u, 0u, 0, 0u);

            vk.CmdEndRenderPass(commandBuffer);

            Check(vk.EndCommandBuffer(commandBuffer));
        }

        private void UpdateModelViewProjection(float time)
        {
            var mvp = new ModelViewProjection
            {
                Model = Matrix4x4.Identity * Matrix4x4.CreateFromAxisAngle(new Vector3(0f, 0f, 1f), time * 90f * MathF.PI / 180f),
                View = Matrix4x4.CreateLookAt(new Vector3(2f, 2f, 2f), new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f)),
                Projection = Matrix4x4.CreatePerspectiveFieldOfView(45f * MathF.PI / 180f, (float)width / height, 0.1f, 10.0f)
            };

            mvp.Projection.M22 *= -1;

            void* data;
            Check(vk.MapMemory(device, uniformMemory, 0ul, (ulong)sizeof(ModelViewProjection), 0u, &data));
            new Span<ModelViewProjection>(data, 1)[0] = mvp;
            vk.UnmapMemory(device, uniformMemory);
        }

        private void SubmitWork()
        {
            fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
            {
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1u,
                    PCommandBuffers = commandBufferPtr
                };

                Check(vk.QueueSubmit(queue, 1u, submitInfo, fence));
            }
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

        public void Initialize(nint directTextureHandle, uint width, uint height)
        {
            this.width = width;
            this.height = height;

            #region Create instance
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                ApiVersion = Vk.Version10
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo
            };

            Check(vk.CreateInstance(createInfo, null, out instance));

            Log.Info($"Instance {instance.Handle} created");
            #endregion

            #region Create device
            uint deviceCount = 0u;

            Check(vk.EnumeratePhysicalDevices(instance, ref deviceCount, null));
            Check(vk.EnumeratePhysicalDevices(instance, ref deviceCount, ref physicalDevice));

            vk.GetPhysicalDeviceProperties(physicalDevice, out var physicalDeviceProperties);

            Log.Info($"GPU: {Encoding.UTF8.GetString(physicalDeviceProperties.DeviceName, 256)}");

            uint queueIndex = GetQueueIndex();
            float queuePriority = 1f;

            var deviceQueueCreateInfo = new DeviceQueueCreateInfo
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
                PQueueCreateInfos = &deviceQueueCreateInfo
            };

            Check(vk.CreateDevice(physicalDevice, deviceCreateInfo, null, out device));

            vk.GetDeviceQueue(device, queueIndex, 0u, out queue);

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };

            Check(vk.CreateFence(device, fenceInfo, null, out fence));
            #endregion

            #region Create command pool
            var commandPoolCreateInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = queueIndex
            };

            Check(vk.CreateCommandPool(device, commandPoolCreateInfo, null, out commandPool));
            #endregion

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
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr
            };

            /*var depthAttachmentDescription = new AttachmentDescription
            {
                Format = Format.D32Sfloat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.DontCare,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            };*/

            var colorAttachmentReference = new AttachmentReference
            {
                Attachment = 0u,
                Layout = ImageLayout.ColorAttachmentOptimal
            };

            /*var depthAttachmentReference = new AttachmentReference
            {
                Attachment = 1u,
                Layout = ImageLayout.DepthStencilAttachmentOptimal,
            };*/

            var subpassDescription = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1u,
                PColorAttachments = &colorAttachmentReference,
                //PDepthStencilAttachment = &depthAttachmentReference
            };

            var subpassDependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit, //| PipelineStageFlags.EarlyFragmentTestsBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit, //| PipelineStageFlags.EarlyFragmentTestsBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit //| AccessFlags.DepthStencilAttachmentWriteBit
            };

            //AttachmentDescription[] attachmentDescriptions = { colorAttachmentDescription, depthAttachmentDescription };

            //fixed (AttachmentDescription* attachmentDescriptionsPtr = attachmentDescriptions)
            //{
                var renderPassCreateInfo = new RenderPassCreateInfo
                {
                    SType = StructureType.RenderPassCreateInfo,
                    AttachmentCount = 1u, //(uint)attachmentDescriptions.Length,
                    //PAttachments = attachmentDescriptionsPtr,
                    PAttachments = &colorAttachmentDescription,
                    SubpassCount = 1u,
                    PSubpasses = &subpassDescription,
                    DependencyCount = 1u,
                    PDependencies = &subpassDependency,
                };

                Check(vk.CreateRenderPass(device, renderPassCreateInfo, null, out renderPass));
            //}
            #endregion

            #region Create vertex, index and uniform buffers
            ulong vertexBufferSize = (ulong)(vertices.Length * sizeof(VertexPositionColor));
            ulong indexBufferSize = (ulong)(indices.Length * sizeof(uint));
            ulong mvpBufferSize = (ulong)sizeof(ModelViewProjection);

            CreateBuffer(BufferUsageFlags.VertexBufferBit, MemoryPropertyFlags.HostVisibleBit,
                out vertexBuffer, out vertexMemory, vertexBufferSize);

            void* data;
            Check(vk.MapMemory(device, vertexMemory, 0ul, vertexBufferSize, 0u, &data));
            vertices.AsSpan().CopyTo(new Span<VertexPositionColor>(data, vertices.Length));
            vk.UnmapMemory(device, vertexMemory);

            CreateBuffer(BufferUsageFlags.IndexBufferBit, MemoryPropertyFlags.HostVisibleBit,
                out indexBuffer, out indexMemory, indexBufferSize);

            Check(vk.MapMemory(device, indexMemory, 0ul, indexBufferSize, 0u, &data));
            indices.AsSpan().CopyTo(new Span<ushort>(data, indices.Length));
            vk.UnmapMemory(device, indexMemory);

            CreateBuffer(BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit,
                out uniformBuffer, out uniformMemory, mvpBufferSize);
            #endregion

            #region Create descriptor pool
            var descriptorPoolSize = new DescriptorPoolSize()
            {
                Type = DescriptorType.UniformBufferDynamic,
                DescriptorCount = 1u
            };

            var descriptorPoolCreateInfo = new DescriptorPoolCreateInfo()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1u,
                PPoolSizes = &descriptorPoolSize,
                MaxSets = 1u
            };

            Check(vk.CreateDescriptorPool(device, descriptorPoolCreateInfo, null, out descriptorPool));
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

            Check(vk.CreateDescriptorSetLayout(device, descriptorSetLayoutCreateInfo, null, out descriptorSetLayout));
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

                Check(vk.AllocateDescriptorSets(device, descriptorSetAllocateInfo, out descriptorSet));
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
                DstArrayElement = 0u,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1u,
                PBufferInfo = &descriptorBufferInfo
            };

            vk.UpdateDescriptorSets(device, 1u, writeDescriptorSet, 0u, null);
            #endregion

            CreateImageView(directTextureHandle);

            CreatePipeline();

            CreateFramebuffer();

            CreateCommandBuffer();
        }

        public void Draw(float time)
        {
            UpdateModelViewProjection(time);
            SubmitWork();
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