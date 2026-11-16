namespace obsidian_graphrag_v2
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new MainPage()) { Title = "obsidian-graphrag-v2" };
        }
    }
}
