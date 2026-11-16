using Microsoft.Extensions.Logging;
using ObsidianGraphRag.Shared;
using obsidian_graphrag_v2.Services;
using obsidian_graphrag_v2.Shared.Services;

namespace obsidian_graphrag_v2
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            // Add device-specific services used by the obsidian_graphrag_v2.Shared project
            builder.Services.AddSingleton<IFormFactor, FormFactor>();

            // Add Obsidian GraphRAG Core Services
            builder.Services.AddObsidianGraphRag(builder.Configuration);

            builder.Services.AddMauiBlazorWebView();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
