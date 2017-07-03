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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kts.ObjectSync.Tests
{
	public class RoundTripTests
	{
		[Fact]
		public async Task Test()
		{
			var serializer = new JsonCommonSerializer();
			var serverObj = new Tester{P2 = 23, P3 = "abc"};
			var serverTransport = new ServerWebSocketTransport(serializer); // never throws
			var serverMgr = new ObjectManager(serverTransport); // never throws
			serverMgr.Add("a", serverObj, true); // never throws
			Console.WriteLine("Starting server...");
			await Startup.StartServer(serverTransport); // should throw if it can't start

			Console.WriteLine("Starting client...");
			var clientTransport = new ClientWebSocketTransport(serializer, new Uri("ws://localhost:15050/ObjSync"));
			var clientMgr = new ObjectManager(clientTransport);
			await clientTransport.HasConnected;

			var clientObj = new Tester();
			clientMgr.Add("a", clientObj);

			await Task.Delay(20);

			Assert.Equal(0, clientObj.P2);
			serverObj.P2 = 42;
			await Task.Delay(20);
			Assert.Equal(42, clientObj.P2);

			// left off: wait for what?
			// 1. We need the ability to send the whole object every time a connection is made.
			// 2. We should bypass any type conversion for the primary JSON types
		}

		public class Startup
		{
			public void ConfigureServices(IServiceCollection services)
			{
			}

			public static Task StartServer(ServerWebSocketTransport transport)
			{
				var tcs = new TaskCompletionSource<bool>();
				var task = new Task(() =>
				{
					var builder = new WebHostBuilder()
						.UseKestrel()
						.UseUrls("http://localhost:15050/")
						//.UseContentRoot(Directory.GetCurrentDirectory())
						//.UseIISIntegration()
						.UseStartup<Startup>()
						//.UseApplicationInsights()
						;
					builder.Configure(app =>
					{
						app.UseDeveloperExceptionPage();
						app.UseWebSockets();
						transport.Attach(app, "/ObjSync");
					});
					var host = builder.Build();
					host.Start();
					var lifetime = host.Services.GetService<IApplicationLifetime>();
					lifetime.ApplicationStarted.WaitHandle.WaitOne();
					tcs.SetResult(true);
					lifetime.ApplicationStopped.WaitHandle.WaitOne();
				}, TaskCreationOptions.LongRunning);
				task.Start();
				return tcs.Task;
			}
		}

		public class Tester : INotifyPropertyChanged
		{
			private double _p1;
			private int _p2;
			private string _p3;
			public event PropertyChangedEventHandler PropertyChanged;

			protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
			{
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
			}

			public double P1
			{
				get => _p1;
				set {
					_p1 = value;
					OnPropertyChanged();
				}
			}

			public int P2
			{
				get => _p2;
				set
				{
					_p2 = value;
					OnPropertyChanged();
				}
			}

			public string P3
			{
				get => _p3;
				set
				{
					_p3 = value;
					OnPropertyChanged();
				}
			}
		}
	}
}
