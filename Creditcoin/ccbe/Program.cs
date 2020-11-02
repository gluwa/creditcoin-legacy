using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace ccbe
{
    internal class Program
    {
        public static ILogger logger;

        public static void Main(string[] args)
        {
            var host = CreateWebHostBuilder(args).Build();
            logger = host.Services.GetRequiredService<ILogger<Program>>();
            var config = host.Services.GetRequiredService<IConfiguration>();
            Cache.creditcoinUrl = config.GetValue<string>("CreditcoinUrl");
            Cache.setTimeout(300000);

            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource())
            {
                ThreadPool.QueueUserWorkItem(new WaitCallback(Caching), cancellationTokenSource.Token);
                host.Run();
                cancellationTokenSource.Cancel();
            }
        }

        private static void Caching(object tokenObject)
        {
            CancellationToken token = (CancellationToken)tokenObject;
            for (; ; )
            {
                if (token.IsCancellationRequested)
                    break;
                Caching();
                Thread.Sleep(1000 * 60 * 5);
            }
        }

        private static void Caching()
        {
            string message = Cache.UpdateCache();
            if (message != null)
                logger.LogError(message);
            message = Cache.UpdateWallets();
            if (message != null)
                logger.LogError(message);
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .UseStartup<Startup>();
    }
}
