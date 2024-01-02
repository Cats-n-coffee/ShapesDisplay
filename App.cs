using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace ShapesDisplay
{
    public class App
    {
        private static IWindow? _window;
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

            _window = Window.Create(options);
            _window.Initialize();

            if (_window.VkSurface is null) throw new Exception("Windowing platform doesn't support Vulkan.");
        }

        private void InitVulkan()
        {

        }

        private void MainLoop()
        {
            _window?.Run();
        }

        private void CleanUp()
        {
            _window?.Dispose();
        }
    }
}
