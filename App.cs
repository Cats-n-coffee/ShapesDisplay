using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.SDL;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Windowing;

namespace ShapesDisplay
{
    unsafe class App
    {
        private static IWindow? _window;
        private static Vk? vk;

        private Instance instance;

        private PhysicalDevice physicalDevice;
        private Device logicalDevice;

        Queue graphicsQueue;

        private ExtDebugUtils? debugUtils;
        private DebugUtilsMessengerEXT debugMessenger; 

        private readonly string[] validationLayers = { "VK_LAYER_KHRONOS_validation" };

        #if DEBUG
        private readonly bool enableValidationLayers = true;
        #else
        private readonly bool enableValidationLayers = false;
        #endif

        struct QueueFamilyIndices
        {
            public uint? GraphicsFamily { get; set; }
            public bool IsComplete() { return GraphicsFamily.HasValue; }
        }

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
                Size = new Vector2D<int>(800, 600),
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
            PickPhysicalDevice();
            CreateLogicalDevice();
        }

        private void MainLoop()
        {
            _window?.Run();
        }

        private void CleanUp()
        {
            if (enableValidationLayers)
            {
                debugUtils?.DestroyDebugUtilsMessenger(instance, debugMessenger, null);
            }

            vk?.DestroyInstance(instance, null);
            vk?.DestroyDevice(logicalDevice, null);
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
                ApiVersion = Vk.Version11,
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
                createInfo.EnabledExtensionCount = (uint)validationLayers.Length;
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
            return indices.IsComplete();
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

            DeviceQueueCreateInfo queueCreateInfo = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = indices.GraphicsFamily!.Value,
                QueueCount = 1,
            };

            float queuePriority = 1.0f;
            queueCreateInfo.PQueuePriorities = &queuePriority;

            PhysicalDeviceFeatures deviceFeatures = new();

            DeviceCreateInfo deviceCreateInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                PQueueCreateInfos = &queueCreateInfo,
                QueueCreateInfoCount = 1,
                PEnabledFeatures = &deviceFeatures,
                EnabledExtensionCount = 0,
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

            if (enableValidationLayers)
            {
                SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledLayerNames);
            }
        }

        #endregion
    }
}
