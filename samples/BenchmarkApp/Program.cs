using System;
using System.IO;
using System.Net;
using System.Runtime;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Benchmarks.Middleware;
using RedHatX.AspNetCore.Server.Kestrel.Transport.Linux;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using System.Linq;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace SampleApp
{
    public class Startup
    {
        public Startup()
        { }

        public void Configure(IApplicationBuilder app, ILoggerFactory loggerFactory)
        {
            app.UsePlainText();
            app.UseJson();
            app.Run(async context =>
            {
                var response = $"hello, world{Environment.NewLine}";
                context.Response.ContentLength = response.Length;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(response);
            });
        }

        public static void Main(string[] args)
        {
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables(prefix: "ASPNETCORE_")
                .AddCommandLine(args)
                .Build();

            var hostBuilder = new WebHostBuilder()
                .UseKestrel((context, options) =>
                {
                    IPEndPoint endPoint = context.Configuration.CreateIPEndPoint();

                    options.Listen(endPoint);
                })
                .UseLinuxTransport()
                .UseStartup<Startup>();

            var host = hostBuilder.Build();
            host.Run();
        }
    }

    static class ConfigurationExtensions
    {
        public static IPEndPoint CreateIPEndPoint(this IConfiguration config)
        {
            var url = config["server.urls"] ?? config["urls"];

            if (string.IsNullOrEmpty(url))
            {
                return new IPEndPoint(IPAddress.Loopback, 8080);
            }

            var address = ServerAddress.FromUrl(url);

            IPAddress ip;

            if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                ip = IPAddress.Loopback;
            }
            else if (!IPAddress.TryParse(address.Host, out ip))
            {
                ip = IPAddress.IPv6Any;
            }

            return new IPEndPoint(ip, address.Port);
        }

    }
}
