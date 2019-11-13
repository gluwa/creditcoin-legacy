using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ccaas
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = CreateWebHostBuilder(args).Build();
            Controllers.CreditcoinController.config = host.Services.GetRequiredService<IConfiguration>();

            var pluginFolder = Controllers.CreditcoinController.config.GetValue<string>("pluginFolder");
            if (!Directory.Exists(pluginFolder))
            {
                var cd = Directory.GetCurrentDirectory();
                pluginFolder = Path.Combine(cd, pluginFolder);
                if (!Directory.Exists(pluginFolder))
                    pluginFolder = cd;
            }
            Controllers.CreditcoinController.pluginFolder = pluginFolder;

            string creditcoinRestApiURL = Controllers.CreditcoinController.config.GetValue<string>("creditcoinRestApiURL");
            Controllers.CreditcoinController.creditcoinUrl = string.IsNullOrWhiteSpace(creditcoinRestApiURL) ? "http://localhost:8008" : creditcoinRestApiURL;

            host.Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>();
    }
}
