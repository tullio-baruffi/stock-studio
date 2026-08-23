using System.Collections.Generic;
using System.IO;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MJ.Classifier.Clients;
using MJ.Classifier.Models;

[assembly: FunctionsStartup(typeof(MJ.Classifier.Startup))]
namespace MJ.Classifier
{
    public class Startup : FunctionsStartup
    {
        public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
        {
            var context = builder.GetContext();

            builder.ConfigurationBuilder
                .AddJsonFile(Path.Combine(context.ApplicationRootPath, "appsettings.json"), optional: true, reloadOnChange: false)
                .AddEnvironmentVariables();
        }

        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddOptions<List<SFTPSettings>>()
                .Configure<IConfiguration>((settings, configuration) =>
                {
                    configuration.GetSection(nameof(SFTPSettings)).Bind(settings);
                });

            builder.Services.AddOptions<List<FTPSettings>>()
                .Configure<IConfiguration>((settings, configuration) =>
                {
                    configuration.GetSection(nameof(FTPSettings)).Bind(settings);
                });

            builder.Services.AddOptions<List<WinSCPFTPSettings>>()
                .Configure<IConfiguration>((settings, configuration) =>
                {
                    configuration.GetSection(nameof(WinSCPFTPSettings)).Bind(settings);
                });

            builder.Services.AddOptions<GenericSettings>()
            .Configure<IConfiguration>((settings, configuration) =>
            {
                configuration.GetSection(nameof(GenericSettings)).Bind(settings);
            });

            builder.Services.AddOptions<SharePointSettings>()
                .Configure<IConfiguration>((settings, configuration) =>
                {
                    configuration.GetSection(nameof(SharePointSettings)).Bind(settings);
                });

            builder.Services.AddOptions<ExiftoolSettings>()
                .Configure<IConfiguration>((settings, configuration) =>
                {
                    configuration.GetSection(nameof(ExiftoolSettings)).Bind(settings);
                });

            builder.Services.AddTransient<SFTPClient>();
            builder.Services.AddTransient<FluentFTPClient>();
            builder.Services.AddTransient<WinSCPFTPClient>();
            builder.Services.AddHttpClient();
        }
    }
}