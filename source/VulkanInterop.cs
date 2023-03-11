using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Interop.Vulkan
{
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
                    Stride = (uint)sizeof(VertexPositionColor),
                    InputRate = VertexInputRate.Vertex,
                    Binding = 0u
                };

                return bindingDescription;
            }

            public static VertexInputAttributeDescription[] GetAttributeDescriptions()
            {
                var attributeDescriptions = new[]
                {
                    new VertexInputAttributeDescription()
                    {
                        Format = Format.R32G32B32Sfloat,
                        Offset = (uint)Marshal.OffsetOf<VertexPositionColor>(nameof(Position)),
                        Binding = 0u,
                        Location = 0u
                    },
                    new VertexInputAttributeDescription()
                    {
                        Format = Format.R32G32B32Sfloat,
                        Offset = (uint)Marshal.OffsetOf<VertexPositionColor>(nameof(Color)),
                        Binding = 0u,
                        Location = 1u
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

        private static byte[] ReadBytes(string filename)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string name = assembly.GetName().Name!;

            using var stream = assembly.GetManifestResourceStream($"{name}.{filename}");
            using var reader = new BinaryReader(stream!);

            return reader.ReadBytes((int)stream!.Length);
        }

        private static void Check(Result result)
        {
            if (result is not Result.Success)
                throw new Exception($"failed to call vulkan function - {result}");
        }

        private bool CheckGraphicsQueue(PhysicalDevice physicalDevice, ref uint index)
        {
            uint queueFamilityCount = 0u;
            vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilityCount, null);

            var queueFamilies = new QueueFamilyProperties[queueFamilityCount];

            fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
                vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref queueFamilityCount, queueFamiliesPtr);

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
            uint proprtyCount = 0u;
            byte layerName = default;
            vk.EnumerateDeviceExtensionProperties(physicalDevice, layerName, ref proprtyCount, null);

            var extensionProperties = new Span<ExtensionProperties>(new ExtensionProperties[proprtyCount]);
            vk.EnumerateDeviceExtensionProperties(physicalDevice, &layerName, &proprtyCount, extensionProperties);

            foreach (var extensionProperty in extensionProperties)
            {
                if (Encoding.UTF8.GetString(extensionProperty.ExtensionName, 256).Trim('\0') == "VK_KHR_external_memory")
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
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit,
                SharingMode = SharingMode.Exclusive,
                MipLevels = 1u,
                ArrayLayers = 1u,
                Extent = new()
                {
                    Width = width, 
                    Height = height,
                    Depth = 1u
                }
            };

            Check(vk.CreateImage(device, &imageCreateInfo, null, out image));

            vk.GetImageMemoryRequirements(device, image, out var memoryRequirements);

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
                MemoryTypeIndex = GetMemoryTypeIndex(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
            };

            Check(vk.AllocateMemory(device, &allocInfo, null, out var deviceMemory));
            Check(vk.BindImageMemory(device, image, deviceMemory, 0ul));

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Vulkan image created: 0x{image.Handle:X16}\n");
            #endregion

            var imageViewCreateInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                ViewType = ImageViewType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Image = image
            };

            Check(vk.CreateImageView(device, imageViewCreateInfo, null, out imageView));
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
                    PVertexAttributeDescriptions = attributeDescriptionsPtr,
                    PVertexBindingDescriptions = &bindingDescription
                };

                var inputAssemblyStateCreateInfo = new PipelineInputAssemblyStateCreateInfo()
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList
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
                    Extent = new(width, height)
                };

                var viewportStateCreateInfo = new PipelineViewportStateCreateInfo()
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    PViewports = &viewport,
                    ViewportCount = 1u,
                    PScissors = &scissorRect,
                    ScissorCount = 1u
                };

                var rasterizationStateCreateInfo = new PipelineRasterizationStateCreateInfo()
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1f
                };

                var multisampleStateCreateInfo = new PipelineMultisampleStateCreateInfo()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                var colorBlendAttachmentState = new PipelineColorBlendAttachmentState()
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };

                var colorBlendStateCreateInfo = new PipelineColorBlendStateCreateInfo()
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOp = LogicOp.Copy,
                    PAttachments = &colorBlendAttachmentState,
                    AttachmentCount = 1u
                };

                var layoutCreateInfo = new PipelineLayoutCreateInfo()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    PSetLayouts = descriptorSetLayoutPtr,
                    SetLayoutCount = 1u
                };

                Check(vk.CreatePipelineLayout(device, layoutCreateInfo, null, out pipelineLayout));

                var pipelineCreateInfo = new GraphicsPipelineCreateInfo()
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PStages = shaderStages,
                    StageCount = 2u,
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
                    Width = width,
                    Height = height,
                    PAttachments = imageViewPtr,
                    AttachmentCount = 1u,
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
                CommandPool = commandPool,
                CommandBufferCount = 1u
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
                PClearValues = &clearColor,
                ClearValueCount = 1u,
                RenderArea =
                {
                    Offset = { X = 0, Y = 0 },
                    Extent = new(width, height)
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
                Projection = Matrix4x4.CreatePerspectiveFieldOfView(45f * MathF.PI / 180f, (float)width / height, 0.1f, 10f)
            };

            mvp.Projection.M22 *= -1f;

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
                    PCommandBuffers = commandBufferPtr,
                    CommandBufferCount = 1u
                };

                Check(vk.QueueSubmit(queue, 1u, submitInfo, fence));
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
                ApiVersion = Vk.Version11
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo
            };

            Check(vk.CreateInstance(createInfo, null, out instance));

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Vulkan instance created: 0x{instance.Handle:X16}");
            #endregion

            #region Pick physical device
            uint queueIndex = 0u;

            uint physicalDeviceCount = 0u;
            Check(vk.EnumeratePhysicalDevices(instance, ref physicalDeviceCount, null));

            var physicalDevices = new Span<PhysicalDevice>(new PhysicalDevice[physicalDeviceCount]);
            Check(vk.EnumeratePhysicalDevices(instance, &physicalDeviceCount, physicalDevices));

            foreach (var physicalDevice in physicalDevices)
            {
                uint proprtyCount = 0u;
                byte layerName = default;
                vk.EnumerateDeviceExtensionProperties(physicalDevice, layerName, ref proprtyCount, null);

                var extensionProperties = new Span<ExtensionProperties>(new ExtensionProperties[proprtyCount]);
                vk.EnumerateDeviceExtensionProperties(physicalDevice, &layerName, &proprtyCount, extensionProperties);

                if (CheckGraphicsQueue(physicalDevice, ref queueIndex) && CheckExternalMemoryExtension(physicalDevice))
                {
                    this.physicalDevice = physicalDevice;
                    break;
                }
            }

            vk.GetPhysicalDeviceProperties(physicalDevice, out var physicalDeviceProperties);

            Console.WriteLine($"{Encoding.UTF8.GetString(physicalDeviceProperties.DeviceName, 256)} physical device having VK_KHR_external_memory extension picked: 0x{physicalDevice.Handle:X16}");
            #endregion

            #region Create device
            float queuePriority = 1f;

            var deviceQueueCreateInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueIndex,
                PQueuePriorities = &queuePriority,
                QueueCount = 1u
            };

            var deviceCreateInfo = new DeviceCreateInfo()
            {
                SType = StructureType.DeviceCreateInfo,
                PQueueCreateInfos = &deviceQueueCreateInfo,
                QueueCreateInfoCount = 1u
            };

            Check(vk.CreateDevice(physicalDevice, deviceCreateInfo, null, out device));

            Console.WriteLine($"Vulkan device created: 0x{device.Handle:X16}");

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

            var colorAttachmentReference = new AttachmentReference
            {
                Attachment = 0u,
                Layout = ImageLayout.ColorAttachmentOptimal
            };

            var subpassDescription = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                PColorAttachments = &colorAttachmentReference,
                ColorAttachmentCount = 1u
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
                PAttachments = &colorAttachmentDescription,
                AttachmentCount = 1u,
                PSubpasses = &subpassDescription,
                SubpassCount = 1u,
                PDependencies = &subpassDependency,
                DependencyCount = 1u
            };

            Check(vk.CreateRenderPass(device, renderPassCreateInfo, null, out renderPass));
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
                PPoolSizes = &descriptorPoolSize,
                PoolSizeCount = 1u,
                MaxSets = 1u
            };

            Check(vk.CreateDescriptorPool(device, descriptorPoolCreateInfo, null, out descriptorPool));
            #endregion

            #region Create descriptor set layout
            var descriptorSetLayoutBinding = new DescriptorSetLayoutBinding()
            {
                DescriptorType = DescriptorType.UniformBuffer,
                StageFlags = ShaderStageFlags.VertexBit,
                Binding = 0u,
                DescriptorCount = 1u
            };

            var descriptorSetLayoutCreateInfo = new DescriptorSetLayoutCreateInfo()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PBindings = &descriptorSetLayoutBinding,
                BindingCount = 1u
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
                    PSetLayouts = setLayoutPtr,
                    DescriptorSetCount = 1u
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
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &descriptorBufferInfo,
                DstBinding = 0u,
                DstArrayElement = 0u,
                DescriptorCount = 1u
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

        public unsafe void Clear()
        {
            vk.DestroyBuffer(device, vertexBuffer, null);
            vk.DestroyBuffer(device, indexBuffer, null);
            vk.DestroyBuffer(device, uniformBuffer, null);

            vk.DestroyRenderPass(device, renderPass, null);

            vk.DestroyShaderModule(device, vertexShaderModule, null);
            vk.DestroyShaderModule(device, fragmentShaderModule, null);

            vk.DestroyDescriptorPool(device, descriptorPool, null);
            vk.DestroyDescriptorSetLayout(device, descriptorSetLayout, null);

            vk.DestroyPipeline(device, pipeline, null);
            vk.DestroyPipelineLayout(device, pipelineLayout, null);

            vk.DestroyImage(device, image, null);
            vk.DestroyImageView(device, imageView, null);

            vk.DestroyFramebuffer(device, framebuffer, null);

            vk.DestroyCommandPool(device, commandPool, null);
            vk.DestroyDevice(device, null);
            vk.DestroyInstance(instance, null);
        }
    }
}