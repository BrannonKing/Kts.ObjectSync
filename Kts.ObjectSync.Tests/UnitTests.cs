using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CommonSerializer.Newtonsoft.Json;
using Kts.ObjectSync.Common;
using Kts.ObjectSync.Transport.AspNetCore;
using Kts.ObjectSync.Transport.ClientWebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kts.ObjectSync.Tests
{
	public class UnitTests
	{
		[Fact]
		public async Task Test()
		{
			var serializer = new JsonCommonSerializer();

			var serverObj = new Tester{P2 = 23, P3 = "abc"};
			var serverTransport = new ServerMiddlewareTransport(serializer);
			var serverMgr = new ObjectManager(serverTransport);
			serverMgr.Add("a", serverObj, true);
			await Startup.RunServer(serverTransport);

			var clientTransport = await ClientWebSocketTransport.Connect(new Uri("http://localhost/"), serializer);
			var clientMgr = new ObjectManager(clientTransport);

			var clientObj = new Tester();
			clientMgr.Add("a", clientObj);

			// left off: wait for what?
			1.We need the ability to send the whole object every time a connection is made.
			2.It would be nice to not have to re-add the transport to the mgr every time it gets disconnected
		}

		public class Startup
		{
			public void ConfigureServices(IServiceCollection services)
			{
			}

			// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
			public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
			{
				loggerFactory.AddConsole();

				if (env.IsDevelopment())
				{
					app.UseDeveloperExceptionPage();
				}

				app.UseWebSockets();

				app.Run(async (context) =>
				{
					await context.Response.WriteAsync("Hello World!");
				});
			}


			public static Task RunServer()
			{
				var tcs = new TaskCompletionSource<bool>();
				Task.Run(() =>
				{
					var host = new WebHostBuilder()
						.UseKestrel()
						//.UseContentRoot(Directory.GetCurrentDirectory())
						//.UseIISIntegration()
						.UseStartup<Startup>()
						//.UseApplicationInsights()
						.Build();

					host.Run();
					tcs.SetResult(true);
				});
				return tcs.Task;
			}
		}

		public class Tester : INotifyPropertyChanged
		{
			public event PropertyChangedEventHandler PropertyChanged;

			protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
			}

			public double P1 { get; set; }
			public int P2 { get; set; }
			public string P3 { get; set; }
		}
	}
}
