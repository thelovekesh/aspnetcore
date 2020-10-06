// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SampleApp
{
    public class Startup
    {
        public void Configure(IApplicationBuilder app, ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger("Default");

            // Add an exception handler that prevents throwing due to large request body size
            app.Use(async (context, next) =>
            {
                // Limit the request body to 1kb
                context.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = 1024;

                try
                {
                    await next.Invoke();
                }
                catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413RequestEntityTooLarge) { }
            });

            app.Run(async context =>
            {
                // Drain the request body
                await context.Request.Body.CopyToAsync(Stream.Null);

                var connectionFeature = context.Connection;
                logger.LogDebug($"Peer: {connectionFeature.RemoteIpAddress?.ToString()}:{connectionFeature.RemotePort}"
                    + $"{Environment.NewLine}"
                    + $"Sock: {connectionFeature.LocalIpAddress?.ToString()}:{connectionFeature.LocalPort}");

                var response = $"hello, world{Environment.NewLine}";
                context.Response.ContentLength = response.Length;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(response);
            });
        }

        public static Task Main(string[] args)
        {
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Console.WriteLine("Unobserved exception: {0}", e.Exception);
            };

            KestrelEventListener.Listener.CollectData();

            var hostBuilder = new HostBuilder()
                .ConfigureWebHost(webHostBuilder =>
                {
                    webHostBuilder
                        .UseKestrel((context, options) =>
                        {
                            if (context.HostingEnvironment.IsDevelopment())
                            {
                                ShowConfig(context.Configuration);
                            }

                            var basePort = context.Configuration.GetValue<int?>("BASE_PORT") ?? 5000;

                            options.ConfigureHttpsDefaults(httpsOptions =>
                            {
                                httpsOptions.SslProtocols = SslProtocols.Tls12;
                            });

                            options.Listen(IPAddress.Loopback, basePort, listenOptions =>
                            {
                                // Uncomment the following to enable Nagle's algorithm for this endpoint.
                                //listenOptions.NoDelay = false;

                                listenOptions.UseConnectionLogging();
                            });

                            options.Listen(IPAddress.Loopback, basePort + 1, listenOptions =>
                            {
                                listenOptions.UseHttps();
                                listenOptions.UseConnectionLogging();
                            });

                            options.ListenLocalhost(basePort + 2, listenOptions =>
                            {
                                // Use default dev cert
                                listenOptions.UseHttps();
                            });

                            options.ListenAnyIP(basePort + 3);

                            options.ListenAnyIP(basePort + 4, listenOptions =>
                            {
                                listenOptions.UseHttps(StoreName.My, "localhost", allowInvalid: true);
                            });

                            options.ListenAnyIP(basePort + 5, listenOptions =>
                            {
                                var localhostCert = CertificateLoader.LoadFromStoreCert("localhost", "My", StoreLocation.CurrentUser, allowInvalid: true);

                                listenOptions.UseHttps((stream, clientHelloInfo, state, cancellationToken) =>
                                {
                                    // Here you would check the name, select an appropriate cert, and provide a fallback or fail for null names.
                                    if (clientHelloInfo.ServerName != null && clientHelloInfo.ServerName != "localhost")
                                    {
                                        throw new AuthenticationException($"The endpoint is not configured for sever name '{clientHelloInfo.ServerName}'.");
                                    }

                                    return new ValueTask<SslServerAuthenticationOptions>(new SslServerAuthenticationOptions
                                    {
                                        ServerCertificate = localhostCert
                                    });
                                }, state: null);
                            });

                            options
                                .Configure()
                                .Endpoint(IPAddress.Loopback, basePort + 6)
                                .LocalhostEndpoint(basePort + 7)
                                .Load();

                            // reloadOnChange: true is the default
                            options
                                .Configure(context.Configuration.GetSection("Kestrel"), reloadOnChange: true)
                                .Endpoint("NamedEndpoint", opt =>
                                {

                                })
                                .Endpoint("NamedHttpsEndpoint", opt =>
                                {
                                    opt.HttpsOptions.SslProtocols = SslProtocols.Tls12;
                                });

                            options.UseSystemd();

                            // The following section should be used to demo sockets
                            //options.ListenUnixSocket("/tmp/kestrel-test.sock");
                        })
                        .UseContentRoot(Directory.GetCurrentDirectory())
                        .UseStartup<Startup>();

                    if (string.Equals(Process.GetCurrentProcess().Id.ToString(), Environment.GetEnvironmentVariable("LISTEN_PID")))
                    {
                        // Use libuv if activated by systemd, since that's currently the only transport that supports being passed a socket handle.
#pragma warning disable CS0618
                        webHostBuilder.UseLibuv(options =>
                        {
                            // Uncomment the following line to change the default number of libuv threads for all endpoints.
                            // options.ThreadCount = 4;
                        });
#pragma warning restore CS0618
                    }
                })
                .ConfigureLogging((_, factory) =>
                {
                    factory.SetMinimumLevel(LogLevel.Warning);
                    factory.AddConsole();
                })
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    var env = hostingContext.HostingEnvironment;
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                          .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: true);
                });

            return hostBuilder.Build().RunAsync();
        }

        private static void ShowConfig(IConfiguration config)
        {
            foreach (var pair in config.GetChildren())
            {
                Console.WriteLine($"{pair.Path} - {pair.Value}");
                ShowConfig(pair);
            }
        }

        public class KestrelEventListener : EventListener
        {
            public static KestrelEventListener Listener = new KestrelEventListener();

            // 'Well Known' GUID for the TPL event source
            private static Guid TPLSOURCEGUID = new Guid("2e5dba47-a3d2-4d16-8ee0-6671ffdcd7b5");

            public void CollectData()
            {
                EventSourceCreated += KestrelEventListener_EventSourceCreated;
                EventWritten += KestrelEventListener_EventWritten;
            }

            private void KestrelEventListener_EventSourceCreated(object sender, EventSourceCreatedEventArgs e)
            {
                if (e.EventSource.Guid == TPLSOURCEGUID)
                {
                    // To get activity tracking, a specific keyword needs to be set before hand
                    // This is done automatically by perfview, but needs to be manually done by other event listeners
                    EnableEvents(e.EventSource, EventLevel.Informational, (EventKeywords)0x80);
                }
                else
                {
                    switch (e.EventSource.Name)
                    {
                        //case "Microsoft-System-Net-Http":
                        //case "Microsoft.AspNetCore.Hosting":
                        //case "Microsoft-Extensions-Logging":
                        //case "Microsoft-System-Net-NameResolution":
                        case "Microsoft-AspNetCore-Server-Kestrel":
                            EnableEvents(e.EventSource, EventLevel.Informational);
                            break;

                        default:
                            //Console.WriteLine($"new EventSource: {e.EventSource.Name}");
                            break;
                    }
                }
            }

            private void KestrelEventListener_EventWritten(object sender, EventWrittenEventArgs e)
            {
                if (e.EventName != "Message" && e.EventName != "FormattedMessage")
                {
                    //ActivityLog.ProcessEvent(e);
                    Console.WriteLine("{0}", JsonSerializer.Serialize(e, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
                }
            }
        }

        internal class ActivityLog
        {
            private static ConcurrentDictionary<Guid, ActivityLog> activeTasks = new ConcurrentDictionary<Guid, ActivityLog>();
            public static List<ActivityLog> CompletedRootTasks = new List<ActivityLog>();

            public string Name;
            public DateTime startTime;
            public DateTime endTime;
            public TimeSpan duration;
            public List<string> messages = new List<string>();
            public ConcurrentBag<ActivityLog> children;
            public ActivityLog parent = null;

            public string ToJson()
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"\t\"Name\": \"{Name}\", ");
                sb.AppendLine($"\t\"Duration\": {duration.Ticks}, ");
                sb.AppendLine($"\t\"Messages\": [\n{string.Join(",\n", messages)}\n], ");
                sb.AppendLine("\t\"Children\": [");
                if (children != null)
                {
                    var first = true;
                    foreach (var child in children)
                    {
                        if (first)
                        {
                            first = false;
                        }
                        else
                        {
                            sb.Append(",");
                        }
                        sb.Append("\t\t" + child.ToJson());
                    }
                }
                sb.AppendLine("]}");
                return sb.ToString();
            }

            public static void ProcessEvent(EventWrittenEventArgs e)
            {
                ActivityLog al;
                if (!activeTasks.TryGetValue(e.ActivityId, out al))
                {
                    al = new ActivityLog();
                    activeTasks.TryAdd(e.ActivityId, al);
                    if (e.RelatedActivityId != Guid.Empty)
                    {
                        ActivityLog parent;
                        if (activeTasks.TryGetValue(e.RelatedActivityId, out parent))
                        {
                            parent.AddChild(al);
                        }
                        else
                        {
                            Console.WriteLine($"{e.RelatedActivityId} cannot be found in list");
                        }
                    }
                }
                al.ProcessEventDetails(e);
            }

            private void ProcessEventDetails(EventWrittenEventArgs e)
            {
                if (e.EventName.EndsWith("Start"))
                {
                    startTime = e.TimeStamp;
                    Name = e.EventName;
                }
                else if (e.EventName.EndsWith("Stop"))
                {
                    endTime = e.TimeStamp;
                    duration = endTime - startTime;
                    ActivityLog entry;
                    activeTasks.TryRemove(e.ActivityId, out entry);
                    if (parent == null)
                    {
                        Console.WriteLine(ToJson());
                    }
                }
                messages.Add(EventToJson(e));
            }

            private void AddChild(ActivityLog child)
            {
                if (children == null)
                {
                    children = new ConcurrentBag<ActivityLog>();
                }
                children.Add(child);
                child.parent = this;
            }

            public static string EventToJson(EventWrittenEventArgs eventWritten)
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"\t\"Source\": \"{eventWritten.EventSource.Name}\", ");
                sb.AppendLine($"\t\"Event\": \"{eventWritten.EventName}\", ");
                sb.AppendLine($"\t\"Message\": \"{JsonEncodedText.Encode(eventWritten.Message ?? "")}\", ");
                sb.AppendLine($"\t\"Level\": \"{eventWritten.Level}\", ");
                sb.AppendLine($"\t\"Ticks\": {eventWritten.TimeStamp.Ticks}, ");
                if (eventWritten.ActivityId != Guid.Empty) { sb.AppendLine($"\t\"ActivityId\": \"{eventWritten.ActivityId}\", "); }
                if (eventWritten.RelatedActivityId != Guid.Empty) { sb.AppendLine($"\t\"ParentId\": \"{eventWritten.RelatedActivityId}\", "); }

                sb.AppendLine("\t\"Payload\": {");

                for (var i = 0; i < eventWritten.PayloadNames.Count; i++)
                {
                    if (i > 0) { sb.AppendLine(","); }
                    sb.AppendLine($"\t\t\"{eventWritten.PayloadNames[i]}\": \"{JsonEncodedText.Encode(eventWritten.Payload[i].ToString() ?? "")}\"");
                }
                sb.AppendLine("\t}");
                sb.AppendLine("}");

                return sb.ToString();
            }
        }
    }
}
