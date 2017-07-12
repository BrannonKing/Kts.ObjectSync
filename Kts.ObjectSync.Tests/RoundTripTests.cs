using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CommonSerializer.Newtonsoft.Json;
using Kts.ObjectSync.Common;
using Kts.ObjectSync.Transport.AspNetCore;
using Kts.ObjectSync.Transport.ClientWebSocket;
using Kts.ObjectSync.Transport.NATS;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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


		[Fact]
		public void TestExpandoProps()
		{
			IDictionary<string, object> obj = new ExpandoObject();
			obj["A"] = "a";
			obj["B"] = 3;

			var accessor = FastMember.TypeAccessor.Create(obj.GetType());
			var members = ((IDynamicMetaObjectProvider)obj).GetMetaObject(Expression.Constant(obj)).GetDynamicMemberNames().ToList();
			Assert.Equal(2, members.Count);
			Assert.NotNull(members.Single(m => m == "A"));
			Assert.Equal("a", accessor[obj, "A"]);
			Assert.Equal(3, accessor[obj, "B"]);
			Assert.NotNull(members.Single(m => m == "B"));
		}

		public class Speedy : DynamicObject, INotifyPropertyChanged
		{
			public override IEnumerable<string> GetDynamicMemberNames()
			{
				for (int i = 0; i < 200; i++)
					yield return "Prop" + i;
			}

			private readonly int[] _props = new int[200];

			public override bool TryGetMember(GetMemberBinder binder, out object result)
			{
				result = 0;
				if (!binder.Name.StartsWith("Prop")) return false;
				if (!int.TryParse(binder.Name.Substring(4), out var idx) || idx < 0 || idx >= 200) return false;
				result = _props[idx];
				return true;
			}

			public override bool TrySetMember(SetMemberBinder binder, object value)
			{
				if (!binder.Name.StartsWith("Prop")) return false;
				if (!int.TryParse(binder.Name.Substring(4), out var idx) || idx < 0 || idx >= 200) return false;
				var oldVal = _props[idx];
				if (oldVal != (int) value)
				{
					if (oldVal > (int) value)
						return true;
						//throw new InvalidOperationException("Out of order");
					_props[idx] = (int) value;
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(binder.Name));
				}
				return true;
			}

			public event PropertyChangedEventHandler PropertyChanged;
		}


		[Fact]
		public async Task SpeedTest()
		{
			var serializer = new JsonCommonSerializer();
			var serverObj = new Speedy();
			var serverTransport = new ServerWebSocketTransport(serializer, 0.0); // never throws
			var serverMgr = new ObjectManager(serverTransport); // never throws
			serverMgr.Add("speedy", serverObj); // never throws
			Console.WriteLine("Starting server...");
			await Startup.StartServer(serverTransport); // should throw if it can't start

			Console.WriteLine("Starting client...");
			var clientTransport = new ClientWebSocketTransport(serializer, new Uri("ws://localhost:15050/ObjSync"), aggregationDelay: 0.0);
			var clientMgr = new ObjectManager(clientTransport);
			await clientTransport.HasConnected;

			var clientObj = new Speedy();
			clientMgr.Add("speedy", clientObj);

			var lastClientVal = -1;
			var lastServerVal = -1;
			var shouldRun = true;
			var t1 = Task.Run(() =>
			{
				var accessor = FastMember.ObjectAccessor.Create(clientObj);
				int j = 0;
				while (shouldRun)
				{
					var idx = j % 100;
					j++;
					lastClientVal = (int) accessor["Prop" + idx] + 1;
					accessor["Prop" + idx] = lastClientVal;
				}
				clientTransport.Flush().ConfigureAwait(false).GetAwaiter().GetResult();
			});
			var t2 = Task.Run(() =>
			{
				var accessor = FastMember.ObjectAccessor.Create(serverObj);
				int j = 0;
				while (shouldRun)
				{
					var idx = (j % 100) + 100;
					j++;
					lastServerVal = (int)accessor["Prop" + idx] + 1;
					accessor["Prop" + idx] = lastServerVal;
				}
				serverTransport.Flush().ConfigureAwait(false).GetAwaiter().GetResult();
			});
			await Task.Delay(8000);
			shouldRun = false;
			await t1;
			await t2;

		}

		[Fact]
		public async Task SpeedTestNats()
		{
			var serializer = new JsonCommonSerializer();
			var serverObj = new Speedy();
			var serverTransport = new NatsTransport(serializer); // never throws
			var serverMgr = new ObjectManager(serverTransport); // never throws
			serverMgr.Add("speedy", serverObj); // never throws
			Console.WriteLine("Starting server...");

			Console.WriteLine("Starting client...");
			var clientTransport = new NatsTransport(serializer);
			var clientMgr = new ObjectManager(clientTransport);

			while (!serverTransport.IsConnected || !clientTransport.IsConnected)
				await Task.Delay(5);

			var clientObj = new Speedy();
			clientMgr.Add("speedy", clientObj);

			var lastClientVal = -1;
			var lastServerVal = -1;
			var shouldRun = true;
			var t1 = Task.Run(() =>
			{
				var accessor = FastMember.ObjectAccessor.Create(clientObj);
				int j = 0;
				while (shouldRun)
				{
					var idx = j % 100;
					j++;
					lastClientVal = (int)accessor["Prop" + idx] + 1;
					accessor["Prop" + idx] = lastClientVal;
				}
				clientTransport.Flush();
			});
			var t2 = Task.Run(() =>
			{
				var accessor = FastMember.ObjectAccessor.Create(serverObj);
				int j = 0;
				while (shouldRun)
				{
					var idx = (j % 100) + 100;
					j++;
					lastServerVal = (int)accessor["Prop" + idx] + 1;
					accessor["Prop" + idx] = lastServerVal;
				}
				serverTransport.Flush();
			});
			await Task.Delay(8000);
			shouldRun = false;
			await t1;
			await t2;

			clientTransport.Dispose();
			serverTransport.Dispose();
		}
	}
}
