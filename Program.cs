using Silk.NET.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;

namespace ShapesDisplay
{
    class Program
    {
        private static IWindow? _window;
        public static void Main()
        {
            WindowOptions options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(800, 600),
                Title = "Shapes Display in C#",
            };

            _window = Window.Create(options);

            _window.Load += OnLoad;
            _window.Update += OnUpdate;
            _window.Render += OnRender;

            _window.Run();
        }

        private static void OnLoad() 
        {
            IInputContext input = _window.CreateInput();
            
            for ( int i = 0; i < input.Keyboards.Count; i++)
            {
                input.Keyboards[i].KeyDown += KeyDown;
            }
        }

        private static void OnUpdate(double deltaTime) { Console.WriteLine("Update"); }

        private static void OnRender(double deltaTime) { Console.WriteLine("Render"); }

        private static void KeyDown(IKeyboard keyboard, Key key, int keyCode)
        {
            if (key == Key.Escape) _window.Close();
        }
    }
}