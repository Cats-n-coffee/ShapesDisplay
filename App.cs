using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.IO;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.SDL;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using System.Diagnostics;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

struct QueueFamilyIndices
{
    public uint? GraphicsFamily { get; set; }
    public uint? PresentFamily { get; set; }
    public bool IsComplete()
    {
        return GraphicsFamily.HasValue && PresentFamily.HasValue;
    }
}

struct SwapChainSupportDetails
{
    public SurfaceCapabilitiesKHR Capabilities;
    public SurfaceFormatKHR[] Formats;
    public PresentModeKHR[] PresentModes;
}

struct Vertex
{
    public Vector2D<float> pos;
    public Vector3D<float> color;

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
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(pos)),
            },
            new VertexInputAttributeDescription()
            {
                Binding = 0,
                Location = 1,
                Format = Format.R32G32B32Sfloat,
                Offset = (uint)Marshal.OffsetOf<Vertex>(nameof(color)),
            }
        };

        return attributeDescriptions;
    }
}

namespace ShapesDisplay
{
    unsafe class App
    {
        const int WIDTH = 800;
        const int HEIGHT = 600;

        const int MAX_FRAMES_IN_FLIGHT = 2;

        private IWindow? _window;
        private Vk? vk;

        private Instance instance;

        private PhysicalDevice physicalDevice;
        private Device logicalDevice;

        private Queue graphicsQueue;
        private Queue presentQueue;

        private SurfaceKHR surface;
        private KhrSurface? khrSurface;

        private KhrSwapchain? khrSwapChain;
        private SwapchainKHR swapChain;
        private Image[]? swapChainImages;
        private Format swapChainImageFormat;
        private Extent2D swapChainExtent;
        private ImageView[]? swapchainImageViews;

        private RenderPass renderPass;
        private PipelineLayout pipelineLayout;

        private Pipeline graphicsPipeline;

        private Framebuffer[]? swapchainFramebuffers;
        private CommandPool commandPool;
        private CommandBuffer[]? commandBuffers;
        private Semaphore[]? imageAvailableSemaphores;
        private Semaphore[]? renderFinishedSemaphores;
        private Fence[]? inFlightFences;
        private Fence[]? imagesInFlight;
        private int currentFrame = 0;
        private bool framebufferResized = false;

        private Vertex[] vertices = new Vertex[]
        {
            new Vertex { pos = new Vector2D<float>(0.0f,-0.5f), color = new Vector3D<float>(1.0f, 1.0f, 1.0f) },
            new Vertex { pos = new Vector2D<float>(0.5f,0.5f), color = new Vector3D<float>(0.0f, 1.0f, 0.0f) },
            new Vertex { pos = new Vector2D<float>(-0.5f,0.5f), color = new Vector3D<float>(0.0f, 0.0f, 1.0f) },
        };

        Buffer vertexBuffer;
        DeviceMemory vertexBufferMemory;

        private ExtDebugUtils? debugUtils;
        private DebugUtilsMessengerEXT debugMessenger;

        private readonly string[] validationLayers = { "VK_LAYER_KHRONOS_validation" };
        private readonly string[] deviceExtensions = { KhrSwapchain.ExtensionName };

#if DEBUG
        private readonly bool enableValidationLayers = true;
#else
        private readonly bool enableValidationLayers = false;
#endif

        public void Run()
        {
            InitWindow();
            InitVulkan();
            MainLoop();
            CleanUp();
        }

        private void InitWindow()
        {
            WindowOptions options = WindowOptions.DefaultVulkan with
            {
                Size = new Vector2D<int>(WIDTH, HEIGHT),
                Title = "Shapes Display in C#",
            };

            _window = Silk.NET.Windowing.Window.Create(options);
            _window.Initialize();

            if (_window.VkSurface is null) throw new Exception("Windowing platform doesn't support Vulkan.");

            _window.Resize += FramebufferResizeCallback;
        }

        private void FramebufferResizeCallback(Vector2D<int> obj)
        {
            framebufferResized = true;
        }

        private void InitVulkan()
        {
            CreateVulkanInstance();
            // SetupDebugMessenger();
            CreateSurface();
            PickPhysicalDevice();
            CreateLogicalDevice();
            CreateSwapChain();
            CreateImageViews();
            CreateRenderPass();
            CreateGraphicsPipeline();
            CreateFramebuffers();
            CreateCommandPool();
            CreateVertexBuffer();
            CreateCommandBuffers();
            CreateSyncObjects();
        }

        private void MainLoop()
        {
            _window!.Render += DrawFrame;
            _window!.Run();
            vk!.DeviceWaitIdle(logicalDevice);
        }

        private void CleanUp()
        {
            CleanUpSwapChain();

            vk!.DestroyBuffer(logicalDevice, vertexBuffer, null);
            vk!.FreeMemory(logicalDevice, vertexBufferMemory, null);

            vk?.DestroyPipeline(logicalDevice, graphicsPipeline, null);
            vk?.DestroyPipelineLayout(logicalDevice, pipelineLayout, null);
            vk?.DestroyRenderPass(logicalDevice, renderPass, null);

            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                vk!.DestroySemaphore(logicalDevice, imageAvailableSemaphores![i], null);
                vk!.DestroySemaphore(logicalDevice, renderFinishedSemaphores![i], null);
                vk!.DestroyFence(logicalDevice, inFlightFences![i], null);
            }

            vk!.DestroyCommandPool(logicalDevice, commandPool, null);

            vk?.DestroyDevice(logicalDevice, null);

            if (enableValidationLayers)
            {
                debugUtils?.DestroyDebugUtilsMessenger(instance, debugMessenger, null);
            }

            khrSurface!.DestroySurface(instance, surface, null);
            vk?.DestroyInstance(instance, null);

            vk?.Dispose();
            _window?.Dispose();
        }

        private void CreateVulkanInstance()
        {
            vk = Vk.GetApi();

            if (enableValidationLayers && !CheckValidationLayerSupport())
            {
                throw new Exception("No support for validation layers");
            }

            ApplicationInfo appInfo = new ApplicationInfo()
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Shapes Display Hello"),
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = (byte*)Marshal.StringToHGlobalAnsi("No Engine"),
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = Vk.Version12,
            };

            InstanceCreateInfo createInfo = new InstanceCreateInfo()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
            };

            var extensions = GetRequiredExtensions();
            createInfo.EnabledExtensionCount = (uint)extensions.Length;
            createInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);

            if (enableValidationLayers)
            {
                createInfo.EnabledLayerCount = (uint)validationLayers.Length;
                createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

                DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new();
                PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
                createInfo.PNext = &debugCreateInfo;
            } else
            {
                createInfo.EnabledLayerCount = 0;
                createInfo.PNext = null;
            }

            if (vk.CreateInstance(createInfo, null, out instance) != Result.Success)
            {
                throw new Exception("Failed to create instance");
            }

            Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
            Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
            SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

            if (enableValidationLayers)
            {
                SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
            }
        }

        private string[] GetRequiredExtensions()
        {
            var glfwExtensions = _window!.VkSurface!.GetRequiredExtensions(out var glfwExtensionCount);
            var extensions = SilkMarshal.PtrToStringArray((nint)glfwExtensions, (int)glfwExtensionCount);

            if (enableValidationLayers)
            {
                return extensions.Append(ExtDebugUtils.ExtensionName).ToArray();
            }

            return extensions;
        }

        #region Validation Layers
        private void PopulateDebugMessengerCreateInfo(ref DebugUtilsMessengerCreateInfoEXT createInfo)
        {
            createInfo.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
            createInfo.MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
                | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
            createInfo.MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
            createInfo.PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
        }

        private bool CheckValidationLayerSupport()
        {
            uint layerCount = 0;
            vk?.EnumerateInstanceLayerProperties(ref layerCount, null);

            var availableLayers = new LayerProperties[layerCount];

            fixed (LayerProperties* availableLayersPointer = availableLayers)
            {
                vk?.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPointer);
            }

            var availableLayerNames = availableLayers.Select(layer => Marshal.PtrToStringAnsi((IntPtr)layer.LayerName)).ToHashSet();

            return validationLayers.All(availableLayerNames.Contains);
        }

        private void SetupDebugMessenger()
        {
            if (!enableValidationLayers) return;

            if (!vk!.TryGetInstanceExtension(instance, out debugUtils)) return;

            DebugUtilsMessengerCreateInfoEXT createInfo = new();
            PopulateDebugMessengerCreateInfo(ref createInfo);

            if (debugUtils!.CreateDebugUtilsMessenger(instance, in createInfo, null, out debugMessenger) != Result.Success)
            {
                throw new Exception("Failed to setup debug messenger");
            }
        }

        private uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT messageSeverity,
            DebugUtilsMessageTypeFlagsEXT messageTypes,
            DebugUtilsMessengerCallbackDataEXT* pCallbackData,
            void* pUserData
        )
        {
            Console.WriteLine($"Validation layer: {Marshal.PtrToStringAnsi((nint)pCallbackData->PMessage)}");

            return Vk.False;
        }
        #endregion

        #region Surface
        private void CreateSurface()
        {
            if (!vk!.TryGetInstanceExtension<KhrSurface>(instance, out khrSurface))
            {
                throw new NotSupportedException("Khr surface extension not found");
            }

            surface = _window!.VkSurface!.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
        }

        #endregion

        #region Physical Device
        private void PickPhysicalDevice()
        {
            uint deviceCount = 0;
            vk!.EnumeratePhysicalDevices(instance, ref deviceCount, null);

            if (deviceCount == 0)
            {
                throw new Exception("Failed to find GPU with Vulkan support");
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* devicesPtr = devices)
            {
                vk!.EnumeratePhysicalDevices(instance, ref deviceCount, devicesPtr);
            }

            foreach (var device in devices)
            {
                if (IsSuitableDevice(device))
                {
                    physicalDevice = device;
                    break;
                }
            }

            if (physicalDevice.Handle == 0)
            {
                throw new Exception("Cannot find a suitable GPU");
            }
        }

        private bool IsSuitableDevice(PhysicalDevice device)
        {
            QueueFamilyIndices indices = FindQueueFamilies(device);

            bool extensionsSupported = CheckDeviceExtensionSupport(device);

            bool swapChainAdequate = false;
            if (extensionsSupported)
            {
                SwapChainSupportDetails swapChainSupport = QuerySwapChainSupport(device);
                swapChainAdequate = swapChainSupport.Formats.Any() && swapChainSupport.PresentModes.Any();
            }

            return indices.IsComplete() && extensionsSupported && swapChainAdequate;
        }

        private bool CheckDeviceExtensionSupport(PhysicalDevice device)
        {
            uint extensionCount = 0;
            vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionCount, null);

            var availableExtensions = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
            {
                vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extensionCount, availableExtensionsPtr);
            }

            var availableExtensionNames = availableExtensions.Select(
                extension => Marshal.PtrToStringAnsi((IntPtr)extension.ExtensionName)
            ).ToHashSet();

            return deviceExtensions.All(availableExtensionNames.Contains);
        }

        private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
        {
            QueueFamilyIndices indices = new();
            uint queueFamilyCount = 0;
            vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);

            var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            {
                vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, queueFamiliesPtr);
            }

            uint i = 0;
            foreach (var queueFamily in queueFamilies)
            {
                if (queueFamily.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                {
                    indices.GraphicsFamily = i;
                }

                khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, surface, out var presentSupport);

                if (presentSupport)
                {
                    indices.PresentFamily = i;
                }

                if (indices.IsComplete()) break;

                i++;
            }

            return indices;
        }

        #endregion

        #region Logical Device
        private void CreateLogicalDevice()
        {
            var indices = FindQueueFamilies(physicalDevice);

            var uniqueQueueFamilies = new[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };
            uniqueQueueFamilies = uniqueQueueFamilies.Distinct().ToArray();

            using var mem = GlobalMemory.Allocate(uniqueQueueFamilies.Length * sizeof(DeviceQueueCreateInfo));
            var queueCreateInfos = (DeviceQueueCreateInfo*)Unsafe.AsPointer(ref mem.GetPinnableReference());

            float queuePriority = 1.0f;
            for (int i = 0; i < uniqueQueueFamilies.Length; i++)
            {
                queueCreateInfos[i] = new()
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = uniqueQueueFamilies[i],
                    QueueCount = 1,
                    PQueuePriorities = &queuePriority,
                };
            }

            PhysicalDeviceFeatures deviceFeatures = new();

            DeviceCreateInfo deviceCreateInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                PQueueCreateInfos = queueCreateInfos,
                QueueCreateInfoCount = (uint)uniqueQueueFamilies.Length,
                PEnabledFeatures = &deviceFeatures,
                EnabledExtensionCount = (uint)deviceExtensions.Length,
                PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions),
            };

            if (enableValidationLayers)
            {
                deviceCreateInfo.EnabledLayerCount = (uint)validationLayers.Length;
                deviceCreateInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
            } else
            {
                deviceCreateInfo.EnabledLayerCount = 0;
            }

            if (vk!.CreateDevice(physicalDevice, in deviceCreateInfo, null, out logicalDevice) != Result.Success)
            {
                throw new Exception("Failed to create logical device");
            }

            vk!.GetDeviceQueue(logicalDevice, indices.GraphicsFamily.Value, 0, out graphicsQueue);
            vk!.GetDeviceQueue(logicalDevice, indices.PresentFamily.Value, 0, out presentQueue);

            if (enableValidationLayers)
            {
                SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledLayerNames);
            }
        }

        #endregion

        #region Swap Chain
        private void CreateSwapChain()
        {
            SwapChainSupportDetails swapChainSupport = QuerySwapChainSupport(physicalDevice);

            SurfaceFormatKHR surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);
            PresentModeKHR presentMode = ChooseSwapPresentMode(swapChainSupport.PresentModes);
            Extent2D swapExtent = ChooseSwapExtent(swapChainSupport.Capabilities);

            uint imageCount = swapChainSupport.Capabilities.MinImageCount + 1;
            if (swapChainSupport.Capabilities.MaxImageCount > 0
                && imageCount > swapChainSupport.Capabilities.MaxImageCount)
            {
                imageCount = swapChainSupport.Capabilities.MaxImageCount;
            }

            SwapchainCreateInfoKHR swapChainCreateInfo = new()
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = swapExtent,
                ImageArrayLayers = 1,
                ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            };

            var indices = FindQueueFamilies(physicalDevice);

            var queueFamilyIndices = stackalloc[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };

            if (indices.GraphicsFamily != indices.PresentFamily)
            {
                swapChainCreateInfo = swapChainCreateInfo with
                {
                    ImageSharingMode = SharingMode.Concurrent,
                    QueueFamilyIndexCount = 2,
                    PQueueFamilyIndices = queueFamilyIndices,
                };
            } else
            {
                swapChainCreateInfo = swapChainCreateInfo with
                {
                    ImageSharingMode = SharingMode.Exclusive,
                    QueueFamilyIndexCount = 0,
                    PQueueFamilyIndices = null,
                };
            }

            swapChainCreateInfo.PreTransform = swapChainSupport.Capabilities.CurrentTransform;
            swapChainCreateInfo.CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr;
            swapChainCreateInfo.PresentMode = presentMode;
            swapChainCreateInfo.Clipped = true;
            swapChainCreateInfo.OldSwapchain = default;

            if (!vk!.TryGetDeviceExtension(instance, logicalDevice, out khrSwapChain))
            {
                throw new NotSupportedException("VK_KHR_swapchain extension not found");
            }

            if (khrSwapChain!.CreateSwapchain(logicalDevice, swapChainCreateInfo, null, out swapChain) != Result.Success)
            {
                throw new Exception("Failed to create swap chain");
            }

            khrSwapChain!.GetSwapchainImages(logicalDevice, swapChain, ref imageCount, null);
            swapChainImages = new Image[imageCount];

            fixed (Image* swapChainImagesPtr = swapChainImages)
            {
                khrSwapChain!.GetSwapchainImages(logicalDevice, swapChain, ref imageCount, swapChainImagesPtr);
            }

            swapChainImageFormat = surfaceFormat.Format;
            swapChainExtent = swapExtent;
        }

        void RecreateSwapChain()
        {
            Vector2D<int> framebufferSize = _window!.FramebufferSize;

            while (framebufferSize.X == 0 &&  framebufferSize.Y == 0)
            {
                framebufferSize = _window.FramebufferSize;
                _window.DoEvents();
            }
            vk!.DeviceWaitIdle(logicalDevice);

            CleanUpSwapChain();

            CreateSwapChain();
            CreateImageViews();
            CreateRenderPass();
            CreateGraphicsPipeline();
            CreateFramebuffers();
            CreateCommandBuffers();

            imagesInFlight = new Fence[swapChainImages!.Length];
        }

        void CleanUpSwapChain()
        {
            foreach (var framebuffer in swapchainFramebuffers!)
            {
                vk!.DestroyFramebuffer(logicalDevice, framebuffer, null);
            }

            foreach (var imageView in swapchainImageViews!)
            {
                vk!.DestroyImageView(logicalDevice, imageView, null);
            }

            khrSwapChain?.DestroySwapchain(logicalDevice, swapChain, null);
        }

        private SwapChainSupportDetails QuerySwapChainSupport(PhysicalDevice device)
        {
            var details = new SwapChainSupportDetails();

            khrSurface!.GetPhysicalDeviceSurfaceCapabilities(device, surface, out details.Capabilities);

            uint formatCount = 0;
            khrSurface!.GetPhysicalDeviceSurfaceFormats(device, surface, ref formatCount, null);

            if (formatCount != 0)
            {
                details.Formats = new SurfaceFormatKHR[formatCount];
                fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
                {
                    khrSurface.GetPhysicalDeviceSurfaceFormats(device, surface, ref formatCount, formatsPtr);
                }
            } else
            {
                details.Formats = Array.Empty<SurfaceFormatKHR>();
            }

            uint presentModesCount = 0;
            khrSurface!.GetPhysicalDeviceSurfacePresentModes(device, surface, ref presentModesCount, null);

            if (presentModesCount != 0)
            {
                details.PresentModes = new PresentModeKHR[presentModesCount];
                fixed (PresentModeKHR* presentModesPtr = details.PresentModes)
                {
                    khrSurface!.GetPhysicalDeviceSurfacePresentModes(device, surface, ref presentModesCount, presentModesPtr);
                }
            }

            return details;
        }

        private SurfaceFormatKHR ChooseSwapSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> availableFormats)
        {
            foreach (var availableFormat in availableFormats)
            {
                if (availableFormat.Format == Format.R8G8B8A8Srgb
                    && availableFormat.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return availableFormat;
                }
            }

            return availableFormats[0];
        }

        private PresentModeKHR ChooseSwapPresentMode(IReadOnlyList<PresentModeKHR> availablePresentModes)
        {
            foreach (var availablePresentMode in availablePresentModes)
            {
                if (availablePresentMode == PresentModeKHR.MailboxKhr)
                {
                    return availablePresentMode;
                }
            }

            return PresentModeKHR.FifoKhr;
        }
        private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                return capabilities.CurrentExtent;
            } else
            {
                var frameBufferSize = _window!.FramebufferSize;

                Extent2D actualExtent = new()
                {
                    Width = (uint)frameBufferSize.X,
                    Height = (uint)frameBufferSize.Y,
                };

                actualExtent.Width = Math.Clamp(
                    actualExtent.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width);
                actualExtent.Height = Math.Clamp(
                    actualExtent.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);

                return actualExtent;
            }
        }

        #endregion

        #region ImageViews
        private void CreateImageViews()
        {
            swapchainImageViews = new ImageView[swapChainImages!.Length];

            for (int i = 0; i < swapChainImages.Length; i++)
            {
                ImageViewCreateInfo imageViewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = swapChainImages[i],
                    ViewType = ImageViewType.Type2D,
                    Format = swapChainImageFormat,
                    Components = {
                        R = ComponentSwizzle.Identity,
                        G = ComponentSwizzle.Identity,
                        B = ComponentSwizzle.Identity,
                        A = ComponentSwizzle.Identity,
                    },
                    SubresourceRange = {
                        AspectMask = ImageAspectFlags.ColorBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                };

                if (vk!.CreateImageView(logicalDevice, imageViewInfo, null, out swapchainImageViews[i]) != Result.Success)
                {
                    throw new Exception("Failed to create image view");
                }
            }
        }

        #endregion

        #region Render Pass
        private void CreateRenderPass()
        {
            AttachmentDescription colorAttachment = new()
            {
                Format = swapChainImageFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
            };

            AttachmentReference colorAttachmentRef = new()
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };

            SubpassDescription subpass = new()
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAttachmentRef,
            };

            SubpassDependency subpassDependency = new()
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = 0,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };

            RenderPassCreateInfo renderPassCreateInfo = new()
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &subpassDependency,
            };

            if (vk!.CreateRenderPass(logicalDevice, renderPassCreateInfo, null, out renderPass) != Result.Success)
            {
                throw new Exception("Failed to create render pass");
            }
        }

        #endregion

        #region Graphics Pipeline
        private void CreateGraphicsPipeline()
        {
            var projectDir = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.ToString();

            var vertShaderCode = File.ReadAllBytes(Path.Combine(projectDir, "Shaders\\vert.spv"));
            var fragShaderCode = File.ReadAllBytes(Path.Combine(projectDir, "Shaders\\frag.spv"));

            ShaderModule vertShaderModule = CreateShaderModule(vertShaderCode);
            ShaderModule fragShaderModule = CreateShaderModule(fragShaderCode);

            PipelineShaderStageCreateInfo vertShaderStageInfo = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main"),
            };

            PipelineShaderStageCreateInfo fragShaderStageInfo = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragShaderModule,
                PName = (byte*)SilkMarshal.StringToPtr("main"),
            };

            var shaderStages = stackalloc[] { vertShaderStageInfo, fragShaderStageInfo };

            var bindingDescription = Vertex.GetBindingDescription();
            var attributeDescriptions = Vertex.GetAttributeDescriptions();

            fixed (VertexInputAttributeDescription* attributeDescriptionsPtr = attributeDescriptions)
            {
                PipelineVertexInputStateCreateInfo vertexInputStateCreateInfo = new()
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &bindingDescription,
                    VertexAttributeDescriptionCount = (uint)attributeDescriptions.Length,
                    PVertexAttributeDescriptions = attributeDescriptionsPtr,
                };

                PipelineInputAssemblyStateCreateInfo assemblyStateCreateInfo = new()
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                    PrimitiveRestartEnable = false,
                };

                Viewport viewport = new()
                {
                    X = 0.0f,
                    Y = 0.0f,
                    Width = swapChainExtent.Width,
                    Height = swapChainExtent.Height,
                    MinDepth = 0.0f,
                    MaxDepth = 1.0f,
                };

                Rect2D scissor = new()
                {
                    Offset = { X = 0, Y = 0 },
                    Extent = swapChainExtent,
                };

                PipelineViewportStateCreateInfo viewportCreateInfo = new()
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };

                PipelineRasterizationStateCreateInfo rasterizerCreateInfo = new()
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = false,
                    RasterizerDiscardEnable = false,
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1.0f,
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.Clockwise,
                    DepthBiasEnable = false,
                    DepthBiasConstantFactor = 0.0f,
                    DepthBiasClamp = 0.0f,
                    DepthBiasSlopeFactor = 0.0f,
                };

                PipelineMultisampleStateCreateInfo multisampleCreateInfo = new()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    SampleShadingEnable = false,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };

                PipelineColorBlendAttachmentState colorBlendAttachement = new()
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                    BlendEnable = false,
                };

                PipelineColorBlendStateCreateInfo colorBlendingCreateInfo = new()
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachement,
                };

                colorBlendingCreateInfo.BlendConstants[0] = 0;
                colorBlendingCreateInfo.BlendConstants[1] = 0;
                colorBlendingCreateInfo.BlendConstants[2] = 0;
                colorBlendingCreateInfo.BlendConstants[3] = 0;

                PipelineLayoutCreateInfo pipelineLayoutCreateInfo = new()
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 0,
                    PushConstantRangeCount = 0,
                };

                if (vk!.CreatePipelineLayout(logicalDevice, pipelineLayoutCreateInfo, null, out pipelineLayout) != Result.Success)
                {
                    throw new Exception("Failed to create pipeline layout");
                }

                GraphicsPipelineCreateInfo pipelineCreateInfo = new()
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInputStateCreateInfo,
                    PInputAssemblyState = &assemblyStateCreateInfo,
                    PViewportState = &viewportCreateInfo,
                    PRasterizationState = &rasterizerCreateInfo,
                    PMultisampleState = &multisampleCreateInfo,
                    PDepthStencilState = null,
                    PColorBlendState = &colorBlendingCreateInfo,
                    Layout = pipelineLayout,
                    RenderPass = renderPass,
                    Subpass = 0,
                    BasePipelineHandle = default,
                };

                if (vk!.CreateGraphicsPipelines(logicalDevice, default, 1, pipelineCreateInfo, null, out graphicsPipeline) != Result.Success)
                {
                    throw new Exception("Failed to create graphics pipeline");
                }
            }

            vk!.DestroyShaderModule(logicalDevice, vertShaderModule, null);
            vk!.DestroyShaderModule(logicalDevice, fragShaderModule, null);

            SilkMarshal.Free((nint)vertShaderStageInfo.PName);
            SilkMarshal.Free((nint)fragShaderStageInfo.PName);
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            ShaderModuleCreateInfo shaderCreateInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
            };

            ShaderModule shaderModule;

            fixed (byte* codePtr = code)
            {
                shaderCreateInfo.PCode = (uint*)codePtr;

                if (vk!.CreateShaderModule(logicalDevice, shaderCreateInfo, null, out shaderModule) != Result.Success)
                {
                    throw new Exception("Failed to create shader module");
                }
            }

            return shaderModule;
        }

        #endregion

        #region Framebuffers
        private void CreateFramebuffers()
        {
            swapchainFramebuffers = new Framebuffer[swapchainImageViews!.Length];

            for (int i = 0; i < swapchainImageViews.Length; i++)
            {
                var attachment = swapchainImageViews[i];

                FramebufferCreateInfo framebufferInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = renderPass,
                    AttachmentCount = 1,
                    PAttachments = &attachment,
                    Width = swapChainExtent.Width,
                    Height = swapChainExtent.Height,
                    Layers = 1,
                };

                if (vk!.CreateFramebuffer(logicalDevice, framebufferInfo, null, out swapchainFramebuffers[i]) != Result.Success)
                {
                    throw new Exception("Failed to create framebuffer");
                }
            }
        }

        #endregion

        #region Command Pool & Buffers
        private void CreateCommandPool()
        {
            QueueFamilyIndices queueFamilyIndices = FindQueueFamilies(physicalDevice);

            CommandPoolCreateInfo commandPoolCreateInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = queueFamilyIndices.GraphicsFamily!.Value,
            };

            if (vk!.CreateCommandPool(logicalDevice, commandPoolCreateInfo, null, out commandPool) != Result.Success)
            {
                throw new Exception("Failed to create command pool");
            }
        }

        private void CreateCommandBuffers()
        {
            commandBuffers = new CommandBuffer[swapchainFramebuffers!.Length];

            CommandBufferAllocateInfo commandBufferInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = (uint)commandBuffers.Length,
            };

            fixed (CommandBuffer* commandBuffersPtr = commandBuffers)
            {
                if (vk!.AllocateCommandBuffers(logicalDevice, commandBufferInfo, commandBuffersPtr) != Result.Success)
                {
                    throw new Exception("Failed to allocate command buffers");
                }
            }

            for (int i = 0; i < commandBuffers.Length; i++)
            {
                CommandBufferBeginInfo commandBufferBeginInfo = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                };

                if (vk!.BeginCommandBuffer(commandBuffers[i], commandBufferBeginInfo) != Result.Success)
                {
                    throw new Exception("Failed to begin recording command buffer");
                }

                RenderPassBeginInfo renderPassBeginInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = renderPass,
                    Framebuffer = swapchainFramebuffers![i],
                    RenderArea = {
                        Offset = { X = 0, Y = 0 },
                        Extent = swapChainExtent,
                    }
                };

                ClearValue clearColor = new()
                {
                    Color = new() { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 },
                };

                renderPassBeginInfo.ClearValueCount = 1;
                renderPassBeginInfo.PClearValues = &clearColor;

                vk!.CmdBeginRenderPass(commandBuffers[i], &renderPassBeginInfo, SubpassContents.Inline);

                vk!.CmdBindPipeline(commandBuffers[i], PipelineBindPoint.Graphics, graphicsPipeline);

                var vertexBuffers = new Buffer[]{ vertexBuffer };
                var offsets = new ulong[] { 0 };

                fixed (ulong* offsetsPtr = offsets)
                fixed (Buffer* vertexBufferPtr = vertexBuffers)
                {
                    vk!.CmdBindVertexBuffers(commandBuffers[i], 0, 1, vertexBufferPtr, offsetsPtr);
                }

                vk!.CmdDraw(commandBuffers[i], (uint)vertices.Length, 1, 0, 0);

                vk!.CmdEndRenderPass(commandBuffers[i]);

                if (vk!.EndCommandBuffer(commandBuffers[i]) != Result.Success)
                {
                    throw new Exception("Failed to record command buffer");
                }
            }

        }

        //private void RecordCommandBuffer(CommandBuffer commandBuf, uint imageIndex)
        //{
        //    CommandBufferBeginInfo commandBufferBeginInfo = new()
        //    {
        //        SType = StructureType.CommandBufferBeginInfo,
        //    };

        //    if (vk!.BeginCommandBuffer(commandBuf, commandBufferBeginInfo) != Result.Success)
        //    {
        //        throw new Exception("Failed to begin recording command buffer");
        //    }

        //    RenderPassBeginInfo renderPassBeginInfo = new()
        //    {
        //        SType = StructureType.RenderPassBeginInfo,
        //        RenderPass = renderPass,
        //        Framebuffer = swapchainFramebuffers[imageIndex],
        //        RenderArea = {
        //                Offset = { X = 0, Y = 0 },
        //                Extent = swapChainExtent,
        //            }
        //    };

        //    ClearValue clearColor = new()
        //    {
        //        Color = new() { Float32_0 = 0, Float32_1 = 0, Float32_2 = 0, Float32_3 = 1 },
        //    };

        //    renderPassBeginInfo.ClearValueCount = 1;
        //    renderPassBeginInfo.PClearValues = &clearColor;

        //    vk!.CmdBeginRenderPass(commandBuf, renderPassBeginInfo, SubpassContents.Inline);
        //    vk!.CmdBindPipeline(commandBuf, PipelineBindPoint.Graphics, graphicsPipeline);
        //    vk!.CmdDraw(commandBuf, 3, 1, 0, 0);
        //    vk!.CmdEndRenderPass(commandBuf);

        //    if (vk!.EndCommandBuffer(commandBuf) != Result.Success)
        //    {
        //        throw new Exception("Failed to record command buffer");
        //    }
        //}
        #endregion

        #region Vertex Buffer
        private void CreateVertexBuffer()
        {
            ulong bufferSize = (ulong)(Unsafe.SizeOf<Vertex>() * vertices.Length);

            Buffer stagingBuffer = default;
            DeviceMemory stagingBufferMemory = default;

            CreateBuffer(bufferSize, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit
                | MemoryPropertyFlags.HostCoherentBit, ref stagingBuffer, ref stagingBufferMemory);

            void* data;
            vk!.MapMemory(logicalDevice, stagingBufferMemory, 0, bufferSize, 0, &data);
            vertices.AsSpan().CopyTo(new Span<Vertex>(data, vertices.Length));
            vk!.UnmapMemory(logicalDevice, stagingBufferMemory);

            CreateBuffer(bufferSize, BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.DeviceLocalBit, ref vertexBuffer, ref vertexBufferMemory);

            CopyBuffer(stagingBuffer, vertexBuffer, bufferSize);

            vk!.DestroyBuffer(logicalDevice, stagingBuffer, null);
            vk!.FreeMemory(logicalDevice, stagingBufferMemory, null);
        }

        private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
            ref Buffer buffer, ref DeviceMemory bufferMemory)
        {
            BufferCreateInfo bufferCreateInfo = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Buffer* bufferPtr = &buffer)
            {
                if (vk!.CreateBuffer(logicalDevice, bufferCreateInfo, null, bufferPtr) != Result.Success)
                {
                    throw new Exception("Failed to create vertex buffer");
                }
            }

            MemoryRequirements memRequirements = new();
            vk!.GetBufferMemoryRequirements(logicalDevice, buffer, out memRequirements);

            MemoryAllocateInfo allocCreateInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memRequirements.Size,
                MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, properties)
            };

            fixed (DeviceMemory* bufferMemoryPtr = &bufferMemory)
            {
                if (vk!.AllocateMemory(logicalDevice, allocCreateInfo, null, bufferMemoryPtr) != Result.Success)
                {
                    throw new Exception("Failed to allocate vertex buffer memory");
                }
            }

            vk!.BindBufferMemory(logicalDevice, buffer, bufferMemory, 0);
        }

        private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
        {
            PhysicalDeviceMemoryProperties memProperties = new();
            vk!.GetPhysicalDeviceMemoryProperties(physicalDevice, out memProperties);

            for (int i = 0; i < memProperties.MemoryTypeCount; i++)
            {
                if ((typeFilter & (1 << i)) != 0 && (memProperties.MemoryTypes[i].PropertyFlags & properties) == properties)
                {
                    return (uint)i;
                }
            }

            throw new Exception("Failed to find suitable memory type");
        }

        private void CopyBuffer(Buffer srcBuffer, Buffer dstBuffer, ulong size)
        {
            CommandBufferAllocateInfo allocCreateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = commandPool,
                CommandBufferCount = 1,
            };

            vk!.AllocateCommandBuffers(logicalDevice, allocCreateInfo, out CommandBuffer commandBuffer);

            CommandBufferBeginInfo beginCreateInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            vk!.BeginCommandBuffer(commandBuffer, beginCreateInfo);

            BufferCopy copyRegion = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = size,
            };

            vk!.CmdCopyBuffer(commandBuffer, srcBuffer, dstBuffer, 1, copyRegion);

            vk!.EndCommandBuffer(commandBuffer);

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };

            vk!.QueueSubmit(graphicsQueue, 1, submitInfo, default);
            vk!.QueueWaitIdle(graphicsQueue);

            vk!.FreeCommandBuffers(logicalDevice, commandPool, 1, commandBuffer);
        }

        #endregion

        #region Draw
        private void DrawFrame(double delta)
        {
            vk!.WaitForFences(logicalDevice, 1, inFlightFences![currentFrame], true, ulong.MaxValue);

            uint indexImage = 0;
            var result = khrSwapChain!.AcquireNextImage(logicalDevice, swapChain, ulong.MaxValue, imageAvailableSemaphores![currentFrame], default, ref indexImage);

            if (result == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapChain();
                return;
            } else if (result != Result.Success && result != Result.SuboptimalKhr)
            {
                throw new Exception("Failed to acquire swap chain image.");
            }

            if (imagesInFlight![indexImage].Handle != default)
            {
                vk!.WaitForFences(logicalDevice, 1, imagesInFlight[indexImage], true, ulong.MaxValue);
            }
            imagesInFlight[indexImage] = inFlightFences[currentFrame];
            

            //vk!.ResetCommandBuffer(commandBuffers![currentFrame], 0);
            //RecordCommandBuffer(commandBuffers![currentFrame], indexImage);

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
            };

            var waitSemaphores = stackalloc[] { imageAvailableSemaphores[currentFrame] };
            var waitStages = stackalloc[]{ PipelineStageFlags.ColorAttachmentOutputBit };

            var buffer = commandBuffers![indexImage];

            submitInfo = submitInfo with
            {
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = 1,
                PCommandBuffers = &buffer,
            };

            var signalSemaphores = stackalloc[] { renderFinishedSemaphores![currentFrame] };

            submitInfo.PSignalSemaphores = signalSemaphores;
            submitInfo.SignalSemaphoreCount = 1;

            vk!.ResetFences(logicalDevice, 1, inFlightFences![currentFrame]);

            if (vk!.QueueSubmit(graphicsQueue, 1, submitInfo, inFlightFences![currentFrame]) != Result.Success)
            {
                throw new Exception("Failed to submit draw command buffer");
            }

            var swapChains = stackalloc[] { swapChain };

            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = signalSemaphores,
                SwapchainCount = 1,
                PSwapchains = swapChains,
                PImageIndices = &indexImage,
            };

            result = khrSwapChain.QueuePresent(presentQueue, presentInfo);

            if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || framebufferResized)
            {
                framebufferResized = false;
                RecreateSwapChain();
            } else if (result != Result.Success)
            {
                throw new Exception("Failed to present swap chain image.");
            }

            currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
        }

        private void CreateSyncObjects()
        {
            imageAvailableSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
            renderFinishedSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
            inFlightFences = new Fence[MAX_FRAMES_IN_FLIGHT];
            imagesInFlight = new Fence[swapChainImages!.Length];

            SemaphoreCreateInfo semaphoreCreateInfo = new()
            {
                SType = StructureType.SemaphoreCreateInfo,
            };

            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };

            for (int i = 0; i < MAX_FRAMES_IN_FLIGHT; i++)
            {
                if (
                vk!.CreateSemaphore(logicalDevice, semaphoreCreateInfo, null, out imageAvailableSemaphores[i]) != Result.Success
                || vk!.CreateSemaphore(logicalDevice, semaphoreCreateInfo, null, out renderFinishedSemaphores[i]) != Result.Success
                || vk!.CreateFence(logicalDevice, fenceCreateInfo, null, out inFlightFences[i]) != Result.Success
            )
                {
                    throw new Exception("Failed to create semaphores or fences for a frame");
                }
            }

            
        }

        #endregion
    }
}
