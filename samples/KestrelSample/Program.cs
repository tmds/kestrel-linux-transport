﻿using System;
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
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;
using System.Linq;

namespace SampleApp
{
    public class Startup
    {
        IConfiguration Configuration;
        static bool _log;
        public Startup()
        {
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            Configuration = configBuilder.Build();
        }
        public void Configure(IApplicationBuilder app, ILoggerFactory loggerFactory)
        {
            if (_log)
            {
                loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            }
            app.UsePlainText();
            app.Run(async context =>
            {
                var response = $"hello, world{Environment.NewLine}";
                context.Response.ContentLength = response.Length;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(response);
            });
        }
	public static int ResponseSize = 0;
        public static void Main(string[] args)
        {
            if (args.Contains("--help"))
            {
                Console.WriteLine("Options: [libuv] [-c<cpuset>] [-t<threadcount>] [ta] [ic] [noda] [nott]");
                Console.WriteLine("  General:");
                Console.WriteLine("\tlibuv    Use libuv Transport instead of Linux Transport");
                Console.WriteLine("\t-t<tc>   Number of transport threads");
                Console.WriteLine("\tnott     Defer requests to thread pool");
                Console.WriteLine("  Linux transport specific:");
                Console.WriteLine("\tta       Set thread affinity");
                Console.WriteLine("\tic       Receive on incoming cpu (implies ta)");
                Console.WriteLine("\t-c<cpus> Cpus for transport threads (implies ta, count = default for -t)");
                Console.WriteLine("\tnoda     No deferred accept");
                Console.WriteLine("\tnods     No deferred send");
                return;
            }

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Console.WriteLine("Unobserved exception: {0}", e.Exception);
            };

            bool libuv = args.Contains("libuv");
            bool ta = args.Contains("ta");
            bool ic = args.Contains("ic");
            bool ds = !args.Contains("nods");
            bool da = !args.Contains("noda");
            bool tt = !args.Contains("nott");
            _log = args.Contains("log");
            int threadCount = 0;
            CpuSet cpuSet = default(CpuSet);
            foreach (var arg in args)
            {
                if (arg.StartsWith("-c"))
                {
                    cpuSet = CpuSet.Parse(arg.Substring(2));
                    ta = true;
                }
                else if (arg.StartsWith("-t"))
                {
                    threadCount = int.Parse(arg.Substring(2));
                }
		else if (arg.StartsWith("-s"))
		{
		    ResponseSize = int.Parse(arg.Substring(2));
		}
            }
            if (ic)
            {
                ta = true;
            }
            if (threadCount == 0)
            {
                threadCount = (libuv || cpuSet.IsEmpty) ? Environment.ProcessorCount : cpuSet.Cpus.Length;
            }

            Console.WriteLine($"Server GC is {(GCSettings.IsServerGC ? "enabled" : "disabled")}");
            if (libuv)
            {
                Console.WriteLine($"Using Libuv: ThreadCount={threadCount}, UseTransportThread={tt}");
            }
            else
            {
                Console.WriteLine($"Using Linux Transport: Cpus={cpuSet}, ThreadCount={threadCount}, IncomingCpu={ic}, SetThreadAffinity={ta}, DeferAccept={da}, UseTransportThread={tt}");
            }

            var hostBuilder = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    options.ApplicationSchedulingMode = tt ? SchedulingMode.Inline : SchedulingMode.ThreadPool;
			options.Listen(IPAddress.Parse("1.1.1.2"), 5000, listenOptions => {} );
                })
                .UseStartup<Startup>();

            if (libuv)
            {
                hostBuilder = hostBuilder.UseLibuv(options => options.ThreadCount = threadCount);
            }
            else
            {
                hostBuilder = hostBuilder.UseLinuxTransport(options =>
                {
                    options.ThreadCount = threadCount;
                    options.SetThreadAffinity = ta;
                    options.ReceiveOnIncomingCpu = ic;
                    options.DeferAccept = da;
                    options.DeferSend = ds;
                    options.CpuSet = cpuSet;
                });
            }

            var host = hostBuilder.Build();
            host.Run();
        }
    }
}
