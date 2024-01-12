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

namespace ShapesDisplay
{
    unsafe class App
    {
        const int WIDTH = 800;
        const int HEIGHT = 600;

        private IWindow? _window;
        private Vk? vk;

        private Instance instance;

        private PhysicalDevice physicalDevice;
        private Device logicalDevice;

        Queue graphicsQueue;
        Queue presentQueue;

        private SurfaceKHR surface;
        private KhrSurface? khrSurface;

        private KhrSwapchain? khrSwapChain;
        private SwapchainKHR swapChain;
        private Image[]? swapChainImages;
        private Format swapChainImageFormat;
        Extent2D swapChainExtent;

        PipelineLayout pipelineLayout;

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
        }

        private void InitVulkan()
        {
            CreateVulkanInstance();
            // SetupDebugMessenger();
            CreateSurface();
            PickPhysicalDevice();
            CreateLogicalDevice();
            CreateSwapChain();
            CreateGraphicsPipeline();
        }

        private void MainLoop()
        {
            _window?.Run();
        }

        private void CleanUp()
        {
            khrSwapChain?.DestroySwapchain(logicalDevice, swapChain, null);
            vk?.DestroyPipelineLayout(logicalDevice, pipelineLayout, null);
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

            fixed(LayerProperties* availableLayersPointer = availableLayers)
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

            foreach(var device in devices)
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
            fixed(QueueFamilyProperties* queueFamiliesPtr = queueFamilies)
            {
                vk!.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, queueFamiliesPtr);
            }
            
            uint i = 0;
            foreach(var queueFamily in queueFamilies)
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

            fixed(Image* swapChainImagesPtr = swapChainImages)
            {
                khrSwapChain!.GetSwapchainImages(logicalDevice, swapChain, ref imageCount, swapChainImagesPtr);
            }

            swapChainImageFormat = surfaceFormat.Format;
            swapChainExtent = swapExtent;
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
            foreach(var availablePresentMode in availablePresentModes)
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

            var shaderStages = stackalloc[]{ vertShaderStageInfo, fragShaderStageInfo };

            PipelineVertexInputStateCreateInfo vertexInputStateCreateInfo = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 0,
                PVertexBindingDescriptions = null,
                VertexAttributeDescriptionCount = 0,
                PVertexAttributeDescriptions = null,
            };

            PipelineInputAssemblyStateCreateInfo assemblyStateCreateInfo = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false,
            };

            Viewport viewport = new()
            {
                X = 0.0f, Y = 0.0f,
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

            PipelineLayoutCreateInfo pipelineCreateInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };

            if (vk!.CreatePipelineLayout(logicalDevice, pipelineCreateInfo, null, out pipelineLayout) != Result.Success)
            {
                throw new Exception("Failed to create pipeline layout");
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
    }
}
