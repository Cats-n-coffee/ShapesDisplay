namespace ShapesDisplay
{
    class Program
    {
        private static App? app;
        
        public static void Main()
        {
            app = new App();

            try
            {
                app.Run();
            } catch (Exception ex)
            {
                Console.WriteLine($"Failure to start the app {ex.Message}");
                throw;
            }
        }
    }
}