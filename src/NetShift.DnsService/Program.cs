using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetShift.DnsService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "NetShift DNS Service");
builder.Services.AddHostedService<DnsProfileWorker>();
await builder.Build().RunAsync();
