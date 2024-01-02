using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace ShapesDisplay
{
    class Program
    {
        private static IWindow _window;
        static void Main(string[] args)
        {
            WindowOptions options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(800, 600),
                Title = "Shapes Display in C#",
            };

            _window = Window.Create(options);
            _window.Run();
        }
    }
}